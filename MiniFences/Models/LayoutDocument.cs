namespace MiniFences.Models;

public sealed class LayoutDocument
{
    public int FormatVersion { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public int CurrentPage { get; set; }
    public int PageCount { get; set; } = 1;
    public List<string> DesktopIconOrder { get; set; } = [];
    public List<FenceConfig> Fences { get; set; } = [];
}

public sealed record LayoutEntry(
    string Id,
    string DisplayName,
    DateTime SavedAtUtc,
    int FenceCount,
    int PageCount);
