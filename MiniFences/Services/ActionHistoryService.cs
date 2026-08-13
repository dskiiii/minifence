using System.IO;
using System.Text.Json;
using MiniFences.Models;

namespace MiniFences.Services;

public sealed class ActionHistoryService
{
    private const int MaxTransactions = 50;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private readonly string _historyPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private ActionJournalDocument _document;

    public ActionHistoryService(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiniFences", "history", "actions.json");
        _document = Load();
        var countBeforePrune = _document.Transactions.Count;
        Prune();
        if (!File.Exists(_historyPath) || _document.Transactions.Count != countBeforePrune) Save();
        if (historyPath is null) TryMigrateLegacyOrganizeHistory();
    }

    public IReadOnlyList<ActionTransaction> GetTransactions()
    {
        lock (_gate)
            return _document.Transactions.OrderByDescending(item => item.CreatedAtUtc).ToList();
    }

    public ActionTransaction? GetNextUndo()
    {
        lock (_gate)
            return _document.Transactions
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault(item => item.IsUndoable &&
                    (item.Status == "Completed" || item.Status == "PartiallyCompleted" ||
                     item.Status == "PartiallyUndone"));
    }

    public void Record(ActionTransaction transaction)
    {
        lock (_gate)
        {
            transaction.Id = string.IsNullOrWhiteSpace(transaction.Id) ? Guid.NewGuid().ToString("N") : transaction.Id;
            transaction.CreatedAtUtc = transaction.CreatedAtUtc == default ? DateTime.UtcNow : transaction.CreatedAtUtc;
            _document.Transactions.Add(transaction);
            Prune();
            Save();
        }
    }

    public List<FenceMembershipSnapshot> CaptureMemberships(AppConfig config) =>
        config.Fences.Where(fence => fence.IsDesktopGroup)
            .Select(fence => new FenceMembershipSnapshot
            {
                FenceId = fence.Id,
                AssignedPaths = [.. fence.AssignedPaths]
            }).ToList();

    public void RecordMembershipChange(string displayName, List<FenceMembershipSnapshot> before, AppConfig after) =>
        Record(new ActionTransaction
        {
            ActionType = "Membership",
            DisplayName = displayName,
            MembershipBefore = before,
            MembershipAfter = CaptureMemberships(after)
        });

    public bool ExecuteMembershipChange(string displayName, AppConfig config, Func<bool> mutation)
    {
        var before = CaptureMemberships(config);
        if (!mutation()) return false;
        RecordMembershipChange(displayName, before, config);
        return true;
    }

    public bool ExecuteFenceDeletion(AppConfig config, FenceConfig fence)
    {
        var index = config.Fences.IndexOf(fence);
        if (index < 0) return false;
        RecordFenceDeleted(fence, index);
        config.Fences.RemoveAt(index);
        return true;
    }

    public bool ExecuteFenceChange(string displayName, AppConfig config, FenceConfig fence, Func<bool> mutation)
    {
        var index = config.Fences.IndexOf(fence);
        if (index < 0) return false;
        var before = Clone(fence);
        if (!mutation()) return false;
        Record(new ActionTransaction
        {
            ActionType = "FenceChange",
            DisplayName = displayName,
            FenceBefore = before,
            FenceAfter = Clone(fence),
            FenceIndex = index
        });
        return true;
    }

    public FolderMoveResult ExecuteFileMove(string displayName, Func<FolderMoveResult> operation)
    {
        var result = operation();
        if (result.Moves?.Count > 0)
            RecordFileMove(displayName,
                result.Moves.Select(move => (move.SourcePath, move.DestinationPath)),
                result.Errors);
        return result;
    }

    public async Task<FolderMoveResult> ExecuteFileMoveAsync(
        string displayName,
        Func<FolderMoveResult> operation)
    {
        var result = await Task.Run(operation);
        if (result.Moves?.Count > 0)
            RecordFileMove(displayName,
                result.Moves.Select(move => (move.SourcePath, move.DestinationPath)),
                result.Errors);
        return result;
    }

    public bool ExecuteRename(FolderItemService service, FolderItem item, string newName,
        out string? renamedPath, out string? error)
    {
        var originalPath = item.FullPath;
        var succeeded = service.TryRenameItem(item, newName, out renamedPath, out error);
        if (succeeded && !string.IsNullOrWhiteSpace(renamedPath) &&
            !PathsEqual(originalPath, renamedPath)) RecordRename(originalPath, renamedPath);
        return succeeded;
    }

    public bool ExecuteRecycleDelete(FolderItemService service, FolderItem item, out string? error)
    {
        var path = item.FullPath;
        var succeeded = service.TryDeleteItem(item, out error);
        if (succeeded) RecordRecycleDelete([path]);
        return succeeded;
    }

    public IReadOnlyList<string> ExecuteRecycleDeleteBatch(FolderItemService service,
        IEnumerable<FolderItem> items, out IReadOnlyList<string> errors)
    {
        var deleted = new List<string>();
        var failures = new List<string>();
        foreach (var item in items)
        {
            if (service.TryDeleteItem(item, out var error)) deleted.Add(item.FullPath);
            else failures.Add($"{item.Name}: {error}");
        }
        if (deleted.Count > 0)
        {
            Record(new ActionTransaction
            {
                ActionType = "RecycleDelete",
                DisplayName = "移到回收站",
                IsUndoable = false,
                Status = failures.Count > 0 ? "PartiallyCompleted" : "Completed",
                Entries = deleted.Select(path => new ActionEntry
                {
                    SourcePath = path,
                    ItemName = Path.GetFileName(path)
                }).ToList(),
                Errors = failures
            });
        }
        errors = failures;
        return deleted;
    }

    public void RecordFenceDeleted(FenceConfig fence, int index) =>
        Record(new ActionTransaction
        {
            ActionType = "DeleteFence",
            DisplayName = $"删除 Fence“{fence.Title}”",
            FenceBefore = Clone(fence),
            FenceIndex = index
        });

    public void RecordFileMove(string displayName, IEnumerable<(string Source, string Destination)> moves,
        IEnumerable<string>? errors = null) =>
        Record(new ActionTransaction
        {
            ActionType = "FileMove",
            DisplayName = displayName,
            Entries = moves.Select(move => new ActionEntry
            {
                SourcePath = move.Source,
                DestinationPath = move.Destination,
                ItemName = Path.GetFileName(move.Source)
            }).ToList(),
            Errors = errors?.ToList() ?? [],
            Status = errors?.Any() == true ? "PartiallyCompleted" : "Completed"
        });

    public void RecordRename(string source, string destination) =>
        RecordFileMove($"重命名“{Path.GetFileName(source)}”", [(source, destination)]);

    public void RecordRecycleDelete(IEnumerable<string> paths) =>
        Record(new ActionTransaction
        {
            ActionType = "RecycleDelete",
            DisplayName = "移到回收站",
            IsUndoable = false,
            Entries = paths.Select(path => new ActionEntry { SourcePath = path, ItemName = Path.GetFileName(path) }).ToList()
        });

    public void RecordLayoutRestore(LayoutDocument before, LayoutDocument after) =>
        Record(new ActionTransaction
        {
            ActionType = "LayoutRestore",
            DisplayName = "恢复布局",
            LayoutBefore = Clone(before),
            LayoutAfter = Clone(after)
        });

    public void RecordLayoutChange(string displayName, LayoutDocument before, LayoutDocument after) =>
        Record(new ActionTransaction
        {
            ActionType = "LayoutRestore",
            DisplayName = displayName,
            LayoutBefore = Clone(before),
            LayoutAfter = Clone(after)
        });

    public ActionUndoResult Undo(string transactionId, AppConfig config, ConfigService configService)
    {
        lock (_gate)
        {
            var transaction = _document.Transactions.FirstOrDefault(item => item.Id == transactionId);
            var next = GetNextUndo();
            if (transaction is null || !transaction.IsUndoable ||
                (transaction.Status != "Completed" && transaction.Status != "PartiallyCompleted" &&
                 transaction.Status != "PartiallyUndone"))
                return new(false, 0, ["该操作不可撤销或已经撤销。"]);
            if (next?.Id != transaction.Id)
                return new(false, 0, ["为避免破坏后续操作，只能按时间顺序撤销上一步。"]);

            var errors = new List<string>();
            var restored = transaction.ActionType switch
            {
                "DeleteFence" => UndoFenceDelete(transaction, config, errors),
                "FenceChange" => UndoFenceChange(transaction, config, errors),
                "Membership" => UndoMembership(transaction, config, errors),
                "FileMove" => UndoFileMoves(transaction, config, errors),
                "LayoutRestore" => UndoLayout(transaction, config, configService, errors),
                _ => 0
            };
            transaction.Errors.AddRange(errors);
            transaction.Status = errors.Count == 0 ? "Undone" :
                transaction.Entries.Any(entry => entry.Status == "Undone") ? "PartiallyUndone" :
                transaction.Errors.Count > 0 ? "PartiallyCompleted" : "Completed";
            if (errors.Count == 0) transaction.UndoneAtUtc = DateTime.UtcNow;
            Save();
            return new(errors.Count == 0, restored, errors);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _document = new ActionJournalDocument();
            Save();
        }
    }

    private static int UndoFenceDelete(ActionTransaction transaction, AppConfig config, List<string> errors)
    {
        var fence = transaction.FenceBefore;
        if (fence is null) { errors.Add("历史中缺少 Fence 状态。"); return 0; }
        if (config.Fences.Any(item => item.Id == fence.Id))
        {
            errors.Add($"Fence“{fence.Title}”已经存在。");
            return 0;
        }
        var index = Math.Clamp(transaction.FenceIndex ?? config.Fences.Count, 0, config.Fences.Count);
        config.Fences.Insert(index, Clone(fence));
        config.PageCount = Math.Max(config.PageCount, fence.PageIndex + 1);
        return 1;
    }

    private static int UndoMembership(ActionTransaction transaction, AppConfig config, List<string> errors)
    {
        var restored = 0;
        foreach (var snapshot in transaction.MembershipBefore)
        {
            var fence = config.Fences.FirstOrDefault(item => item.Id == snapshot.FenceId);
            if (fence is null) { errors.Add($"Fence {snapshot.FenceId} 已不存在。"); continue; }
            fence.AssignedPaths = [.. snapshot.AssignedPaths];
            restored += snapshot.AssignedPaths.Count;
        }
        return restored;
    }

    private static int UndoFenceChange(ActionTransaction transaction, AppConfig config, List<string> errors)
    {
        var fence = transaction.FenceBefore;
        if (fence is null)
        {
            errors.Add("历史中缺少 Fence 状态。");
            return 0;
        }

        var index = config.Fences.FindIndex(item => string.Equals(item.Id, fence.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            errors.Add($"Fence“{fence.Title}”已不存在。");
            return 0;
        }

        config.Fences[index] = Clone(fence);
        config.PageCount = Math.Max(config.PageCount, fence.PageIndex + 1);
        return 1;
    }

    private static int UndoFileMoves(ActionTransaction transaction, AppConfig config, List<string> errors)
    {
        var restored = 0;
        foreach (var entry in transaction.Entries.AsEnumerable().Reverse())
        {
            if (entry.Status == "Undone") continue;
            var source = entry.SourcePath;
            var destination = entry.DestinationPath;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) continue;
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                errors.Add($"{Path.GetFileName(destination)}：当前位置不存在。");
                continue;
            }
            if (File.Exists(source) || Directory.Exists(source))
            {
                errors.Add($"{Path.GetFileName(source)}：原位置已有同名项目，未覆盖。");
                continue;
            }
            try
            {
                var parent = Path.GetDirectoryName(source);
                if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                {
                    errors.Add($"{Path.GetFileName(source)}：原目录不存在。");
                    continue;
                }
                if (File.Exists(destination)) File.Move(destination, source);
                else Directory.Move(destination, source);
                foreach (var fence in config.Fences)
                    for (var i = 0; i < fence.AssignedPaths.Count; i++)
                        if (string.Equals(fence.AssignedPaths[i], destination, StringComparison.OrdinalIgnoreCase))
                            fence.AssignedPaths[i] = source;
                restored++;
                entry.Status = "Undone";
                entry.Error = null;
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                errors.Add($"{Path.GetFileName(source)}：{ex.Message}");
            }
        }
        return restored;
    }

    private static int UndoLayout(ActionTransaction transaction, AppConfig config, ConfigService configService, List<string> errors)
    {
        if (transaction.LayoutBefore is null) { errors.Add("历史中缺少恢复前布局。"); return 0; }
        configService.ApplyLayout(config, Clone(transaction.LayoutBefore), out _);
        return transaction.LayoutBefore.Fences.Count;
    }

    private ActionJournalDocument Load()
    {
        try
        {
            if (!File.Exists(_historyPath)) return new();
            var value = JsonSerializer.Deserialize<ActionJournalDocument>(File.ReadAllText(_historyPath), _jsonOptions);
            if (value?.FormatVersion != 1) throw new InvalidDataException("Unsupported action journal version.");
            return value;
        }
        catch (Exception ex)
        {
            try
            {
                var corrupt = _historyPath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt";
                if (File.Exists(_historyPath)) File.Move(_historyPath, corrupt);
            }
            catch { }
            AppLogger.LogException("Action history was corrupt and has been isolated.", ex);
            var empty = new ActionJournalDocument();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(empty, _jsonOptions));
            }
            catch { }
            return empty;
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - MaxAge;
        _document.Transactions = _document.Transactions
            .Where(item => item.CreatedAtUtc >= cutoff)
            .Where(item => item.ActionType != "FileMove" ||
                           item.Entries.Any(entry => !PathsEqual(entry.SourcePath, entry.DestinationPath)))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(MaxTransactions)
            .OrderBy(item => item.CreatedAtUtc)
            .ToList();
    }

    internal static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_historyPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _historyPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_document, _jsonOptions));
        File.Move(temporary, _historyPath, true);
    }

    private void TryMigrateLegacyOrganizeHistory()
    {
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiniFences", "organize-history.json");
        if (!File.Exists(legacyPath) || _document.Transactions.Any(item => item.ActionType == "LegacyOrganize")) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (!json.RootElement.TryGetProperty("Moves", out var movesElement)) return;
            var entries = new List<ActionEntry>();
            foreach (var move in movesElement.EnumerateArray())
            {
                var source = move.TryGetProperty("SourcePath", out var sourceValue) ? sourceValue.GetString() : null;
                var destination = move.TryGetProperty("DestinationPath", out var destinationValue) ? destinationValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) continue;
                entries.Add(new ActionEntry { SourcePath = source, DestinationPath = destination, ItemName = Path.GetFileName(source) });
            }
            if (entries.Count == 0) return;
            _document.Transactions.Add(new ActionTransaction
            {
                ActionType = "LegacyOrganize",
                DisplayName = "旧版自动整理",
                Entries = entries
            });
            // Legacy physical moves use the same safe inverse rules as current file moves.
            _document.Transactions[^1].ActionType = "FileMove";
            Prune();
            Save();
            File.Move(legacyPath, legacyPath + ".migrated", true);
        }
        catch (Exception ex) { AppLogger.LogException("Failed to migrate legacy organize history.", ex); }
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
