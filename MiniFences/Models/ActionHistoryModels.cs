namespace MiniFences.Models;

public sealed class ActionJournalDocument
{
    public int FormatVersion { get; set; } = 1;
    public List<ActionTransaction> Transactions { get; set; } = [];
}

public sealed class ActionTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActionType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Completed";
    public bool IsUndoable { get; set; } = true;
    public DateTime? UndoneAtUtc { get; set; }
    public List<ActionEntry> Entries { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public FenceConfig? FenceBefore { get; set; }
    public FenceConfig? FenceAfter { get; set; }
    public int? FenceIndex { get; set; }
    public List<FenceMembershipSnapshot> MembershipBefore { get; set; } = [];
    public List<FenceMembershipSnapshot> MembershipAfter { get; set; } = [];
    public LayoutDocument? LayoutBefore { get; set; }
    public LayoutDocument? LayoutAfter { get; set; }

    public int AffectedCount => Entries.Count > 0 ? Entries.Count :
        FenceBefore is not null ? Math.Max(1, FenceBefore.AssignedPaths.Count) :
        MembershipBefore.Sum(item => item.AssignedPaths.Count);

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime DisplayTime => CreatedAtUtc.ToLocalTime();
}

public sealed class ActionEntry
{
    public string? SourcePath { get; set; }
    public string? DestinationPath { get; set; }
    public string? ItemName { get; set; }
    public string Status { get; set; } = "Completed";
    public string? Error { get; set; }
}

public sealed class FenceMembershipSnapshot
{
    public string FenceId { get; set; } = "";
    public List<string> AssignedPaths { get; set; } = [];
}

public sealed record ActionUndoResult(bool Succeeded, int RestoredCount, IReadOnlyList<string> Errors);
