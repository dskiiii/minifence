using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MiniFences.Services;

public sealed record GitHubUpdateInfo(Version Version, string TagName, string ReleaseTitle,
    string ReleaseNotes, string ReleasePageUrl, string AssetName, Uri AssetUri,
    string? Sha256, Uri? ChecksumUri);

public sealed record GitHubUpdateCheckResult(GitHubUpdateInfo? Update, bool IsAvailable, string? Error)
{
    public static GitHubUpdateCheckResult NoUpdate() => new(null, false, null);
    public static GitHubUpdateCheckResult Failed(string error) => new(null, false, error);
}

public sealed class GitHubUpdateService
{
    internal const string RepositoryOwner = "dskiiii";
    internal const string RepositoryName = "minifence";
    internal const string LatestReleaseEndpoint = "https://api.github.com/repos/dskiiii/minifence/releases/latest";
    private const string LatestReleasePage = "https://github.com/dskiiii/minifence/releases/latest";
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(75);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;

    public GitHubUpdateService(HttpClient? httpClient = null, string? cachePath = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _cachePath = cachePath ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "MiniFences", "update-cache.json");
    }

    public async Task<GitHubUpdateCheckResult> CheckForUpdateAsync(Version? currentVersion = null,
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        currentVersion ??= GetCurrentVersion();
        var cache = LoadCache();
        if (!forceRefresh && cache is not null &&
            DateTimeOffset.UtcNow - cache.LastCheckedUtc < AutomaticCheckInterval)
            return GitHubUpdateCheckResult.NoUpdate();

        Exception? apiFailure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            if (!string.IsNullOrWhiteSpace(cache?.ETag) && EntityTagHeaderValue.TryParse(cache.ETag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await SendMetadataRequestAsync(request, cancellationToken);
            string json;
            if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.ReleaseJson))
                json = cache.ReleaseJson;
            else
            {
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            SaveCache(new UpdateCache(DateTimeOffset.UtcNow,
                response.Headers.ETag?.ToString() ?? cache?.ETag, json));
            var release = ParseRelease(JsonDocument.Parse(json).RootElement, currentVersion);
            return release is null ? GitHubUpdateCheckResult.NoUpdate() : new(release, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            apiFailure = ex;
            AppLogger.LogException("GitHub API update check failed; trying the official release redirect", ex);
        }

        try
        {
            var release = await CheckViaOfficialReleaseRedirectAsync(currentVersion, cancellationToken);
            SaveCache(new UpdateCache(DateTimeOffset.UtcNow, cache?.ETag, cache?.ReleaseJson));
            return release is null ? GitHubUpdateCheckResult.NoUpdate() : new(release, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.LogException("GitHub official release fallback failed", ex);
            return GitHubUpdateCheckResult.Failed($"GitHub connection failed: {apiFailure?.Message ?? ex.Message}");
        }
    }

    public async Task<string> DownloadAndVerifyAsync(GitHubUpdateInfo update,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MiniFences", "Updates");
        Directory.CreateDirectory(directory);
        CleanupOldDownloads(directory);
        var finalPath = Path.Combine(directory, update.AssetName);
        var partialPath = finalPath + ".download";

        var expectedHash = NormalizeSha256(update.Sha256);
        if (expectedHash.Length == 0 && update.ChecksumUri is not null)
            expectedHash = await DownloadChecksumAsync(update.ChecksumUri, cancellationToken);
        if (expectedHash.Length == 0)
            throw new InvalidDataException("The update has no valid SHA-256 checksum and will not be installed.");

        await DownloadWithResumeAsync(update.AssetUri, partialPath, progress, cancellationToken);
        var actualHash = await ComputeSha256Async(partialPath, cancellationToken);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partialPath);
            throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
        }
        File.Move(partialPath, finalPath, overwrite: true);
        return finalPath;
    }

    internal static GitHubUpdateInfo? ParseReleaseForTesting(string json, Version currentVersion) =>
        ParseRelease(JsonDocument.Parse(json).RootElement, currentVersion);

    internal static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = value.Trim();
        if (result.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) result = result[7..];
        result = result.Trim().ToLowerInvariant();
        return result.Length == 64 && result.All(Uri.IsHexDigit) ? result : "";
    }

    internal static string LocalizeReleaseNotes(string? releaseNotes, bool chinese)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes)) return "";
        var normalized = releaseNotes.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var lines = normalized.Split('\n');
        var desiredLanguage = chinese ? "zh" : "en";
        string? activeLanguage = null;
        var foundLanguageHeading = false;
        var selectedLines = new List<string>();

        foreach (var line in lines)
        {
            var headingLanguage = GetReleaseNotesHeadingLanguage(line);
            if (headingLanguage is not null)
            {
                foundLanguageHeading = true;
                activeLanguage = headingLanguage;
                continue;
            }

            if (string.Equals(activeLanguage, desiredLanguage, StringComparison.Ordinal))
                selectedLines.Add(line);
        }

        if (foundLanguageHeading)
            return TrimReleaseNoteLines(selectedLines);

        // Releases published before language headings were introduced used a
        // Markdown horizontal rule between the Chinese and English sections.
        var dividerIndex = Array.FindIndex(lines, line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (dividerIndex > 0 && dividerIndex < lines.Length - 1)
        {
            var beforeDivider = TrimReleaseNoteLines(lines.Take(dividerIndex));
            var afterDivider = TrimReleaseNoteLines(lines.Skip(dividerIndex + 1));
            if (ContainsCjkText(beforeDivider) && ContainsSubstantialLatinText(afterDivider))
                return chinese ? beforeDivider : afterDivider;
        }

        return normalized;
    }

    private static string? GetReleaseNotesHeadingLanguage(string line)
    {
        var heading = line.Trim();
        if (!heading.StartsWith('#')) return null;
        heading = heading.TrimStart('#').Trim();
        if (heading.Equals("中文", StringComparison.OrdinalIgnoreCase) ||
            heading.Equals("简体中文", StringComparison.OrdinalIgnoreCase) ||
            heading.Equals("Chinese", StringComparison.OrdinalIgnoreCase)) return "zh";
        if (heading.Equals("英文", StringComparison.OrdinalIgnoreCase) ||
            heading.Equals("English", StringComparison.OrdinalIgnoreCase)) return "en";
        return null;
    }

    private static string TrimReleaseNoteLines(IEnumerable<string> lines)
    {
        var values = lines.ToList();
        while (values.Count > 0 && string.IsNullOrWhiteSpace(values[0])) values.RemoveAt(0);
        while (values.Count > 0 && string.IsNullOrWhiteSpace(values[^1])) values.RemoveAt(values.Count - 1);
        return string.Join(Environment.NewLine, values).Trim();
    }

    private static bool ContainsCjkText(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static bool ContainsSubstantialLatinText(string value) =>
        value.Count(character => character <= 127 && char.IsLetter(character)) >= 20;

    private static GitHubUpdateInfo? ParseRelease(JsonElement root, Version currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        var tagName = GetString(root, "tag_name");
        var version = ParseVersion(tagName);
        if (version is null || version <= currentVersion) return null;
        var assets = root.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray() : [];
        var prefix = $"MiniFences-{GetRuntimeName()}-{version}";
        var package = assets.FirstOrDefault(asset => string.Equals(GetString(asset, "name"),
            $"{prefix}.zip", StringComparison.OrdinalIgnoreCase));
        if (package.ValueKind != JsonValueKind.Object ||
            !Uri.TryCreate(GetString(package, "browser_download_url"), UriKind.Absolute, out var assetUri)) return null;
        var assetName = GetString(package, "name");
        if (string.IsNullOrWhiteSpace(assetName)) return null;
        var digest = NormalizeSha256(GetString(package, "digest"));
        var checksum = assets.FirstOrDefault(asset => string.Equals(GetString(asset, "name"),
            assetName + ".sha256", StringComparison.OrdinalIgnoreCase));
        Uri? checksumUri = null;
        if (checksum.ValueKind == JsonValueKind.Object)
            Uri.TryCreate(GetString(checksum, "browser_download_url"), UriKind.Absolute, out checksumUri);
        return new(version, tagName!, GetString(root, "name") ?? tagName!, GetString(root, "body") ?? "",
            GetString(root, "html_url") ?? $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases",
            assetName, assetUri, digest.Length == 0 ? null : digest, checksumUri);
    }

    private async Task<GitHubUpdateInfo?> CheckViaOfficialReleaseRedirectAsync(
        Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleasePage);
        using var response = await SendMetadataRequestAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? new Uri(LatestReleasePage);
        var tagName = Uri.UnescapeDataString(finalUri.Segments.LastOrDefault()?.Trim('/') ?? "");
        var version = ParseVersion(tagName);
        if (version is null || version <= currentVersion) return null;
        var assetName = $"MiniFences-{GetRuntimeName()}-{version}.zip";
        var releaseBase = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/download/{Uri.EscapeDataString(tagName)}";
        return new(version, tagName, $"MiniFences {version}", "", finalUri.ToString(), assetName,
            new Uri($"{releaseBase}/{assetName}"), null, new Uri($"{releaseBase}/{assetName}.sha256"));
    }

    private async Task DownloadWithResumeAsync(Uri uri, string path, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TryDelete(path);
                    throw new InvalidDataException("The remote update changed while resuming the download.");
                }
                response.EnsureSuccessStatusCode();
                var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!append) existing = 0;
                var totalExpected = response.Content.Headers.ContentLength is > 0
                    ? existing + response.Content.Headers.ContentLength.Value : (long?)null;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 128 * 1024,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
                var buffer = new byte[128 * 1024];
                var total = existing;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).AsTask()
                        .WaitAsync(DownloadIdleTimeout, cancellationToken);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    if (totalExpected is > 0) progress?.Report((double)total / totalExpected.Value);
                }
                await output.FlushAsync(cancellationToken);
                if (totalExpected is > 0 && total != totalExpected.Value)
                    throw new InvalidDataException("The downloaded update was incomplete.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastFailure = ex;
                AppLogger.LogException($"Update download attempt {attempt} failed", ex);
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
        throw new HttpRequestException("The update download failed after four resumable attempts.", lastFailure);
    }

    private async Task<string> DownloadChecksumAsync(Uri uri, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await SendMetadataRequestAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                return text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeSha256).FirstOrDefault(value => value.Length == 64) ?? "";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failure = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
        throw new HttpRequestException("The SHA-256 checksum could not be downloaded.", failure);
    }

    private async Task<HttpResponseMessage> SendMetadataRequestAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private UpdateCache? LoadCache()
    {
        try { return File.Exists(_cachePath) ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(_cachePath)) : null; }
        catch (Exception ex) { AppLogger.LogException("Failed to read the update cache", ex); return null; }
    }

    private void SaveCache(UpdateCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
            File.Move(temporary, _cachePath, true);
        }
        catch (Exception ex) { AppLogger.LogException("Failed to save the update cache", ex); }
    }

    private static string GetRuntimeName() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "win-arm64", Architecture.X64 => "win-x64",
        _ => throw new PlatformNotSupportedException("Updates support only x64 and ARM64 Windows.")
    };
    private static Version GetCurrentVersion() => Assembly.GetEntryAssembly()?.GetName().Version ?? new(0, 0, 0);
    private static Version? ParseVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;
        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return Version.TryParse(value, out var version) ? version : null;
    }
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, token)).ToLowerInvariant();
    }
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MiniFences", "0.24"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    private static void CleanupOldDownloads(string directory)
    {
        try { foreach (var file in Directory.EnumerateFiles(directory))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) TryDelete(file); }
        catch { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private sealed record UpdateCache(DateTimeOffset LastCheckedUtc, string? ETag, string? ReleaseJson);
}
