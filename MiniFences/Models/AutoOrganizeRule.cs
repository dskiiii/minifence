namespace MiniFences.Models;

public sealed class AutoOrganizeRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string TargetFenceId { get; set; } = "";
    public string NamePattern { get; set; } = "";
    public string Extensions { get; set; } = "";
    public bool FoldersOnly { get; set; }
    public double? MinimumSizeMb { get; set; }
    public double? MaximumSizeMb { get; set; }
}
