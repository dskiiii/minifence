namespace MiniFences.Models;

public sealed class FenceConfig
{
    public const string DesktopGroupKind = "DesktopGroup";
    public const string FolderPortalKind = "FolderPortal";

    public string Id { get; set; } = "default";
    public string Title { get; set; } = "Mini Fence";
    public string FolderPath { get; set; } = "";
    public string Kind { get; set; } = FolderPortalKind;
    public List<string> AssignedPaths { get; set; } = [];
    public int PageIndex { get; set; }
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 420;
    public int LayerOrder { get; set; }
    public double? ExpandedHeight { get; set; }
    public string BackgroundColor { get; set; } = "#DD20242A";
    public string HeaderColor { get; set; } = "#CC3F7FA8";
    public bool HeaderGradientEnabled { get; set; }
    public string HeaderGradientColor { get; set; } = "#CC8E5BB7";
    public double Opacity { get; set; } = 1.0;
    public string TitleAlignment { get; set; } = "Left";
    public bool ShowPath { get; set; } = true;
    public bool UseCleanStyle { get; set; }
    public string SortMode { get; set; } = "None";
    public bool IsLocked { get; set; }
    public bool IsCollapsed { get; set; }
    public bool EnableHoverExpand { get; set; }
    public string? EdgeDock { get; set; }
    public string? TabGroupId { get; set; }
    public double? PreTabWidth { get; set; }
    public double? PreTabHeight { get; set; }
    public string? PortalCurrentPath { get; set; }
    public string PortalViewMode { get; set; } = "Icons";
    public double PortalIconSize { get; set; } = 42;
    public double PortalItemSpacing { get; set; } = 4;

    [System.Text.Json.Serialization.JsonIgnore]
    public int DisplayPage => PageIndex + 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDesktopGroup => string.Equals(Kind, DesktopGroupKind, StringComparison.OrdinalIgnoreCase);
}
