namespace MiniFences.Models;

public sealed class AppConfig
{
    public int CurrentPage { get; set; }
    public int PageCount { get; set; } = 1;
    public bool FencesHidden { get; set; }
    public bool EnableDesktopDoubleClick { get; set; } = true;
    public bool EnableDesktopIconIntegration { get; set; } = true;
    public bool EnableSnapToGrid { get; set; } = true;
    public string TabViewMode { get; set; } = "Compact";
    public string TabWidthMode { get; set; } = "Content";
    public bool EnableTabCreation { get; set; } = true;
    public bool ConfirmTabCreation { get; set; }
    public bool HoverSwitchTabs { get; set; }
    public bool EnableRollup { get; set; } = true;
    public bool DoubleClickTitleRollup { get; set; } = true;
    public bool AutoRollupAtScreenEdge { get; set; }
    public bool ClickTitleToExpand { get; set; }
    public bool HoverTitleToExpand { get; set; }
    public string PreviousPageHotkey { get; set; } = "Ctrl+Alt+Left";
    public string NextPageHotkey { get; set; } = "Ctrl+Alt+Right";
    public string ToggleTopmostHotkey { get; set; } = "Ctrl+Alt+Space";
    public bool EnableAutoOrganizeNewDesktopItems { get; set; }
    public string ClassificationScheme { get; set; } = "Detailed";
    public string DefaultAutoOrganizeFenceId { get; set; } = "";
    public List<AutoOrganizeRule> AutoOrganizeRules { get; set; } = [];
    public string Language { get; set; } = "en";
    public Dictionary<string, DesktopIconPositionConfig> DesktopIconPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DesktopIconOrder { get; set; } = [];
    public List<FenceConfig> Fences { get; set; } = [];
}
