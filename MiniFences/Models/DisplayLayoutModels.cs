namespace MiniFences.Models;

public sealed class DisplayLayoutDocument
{
    public int FormatVersion { get; set; } = 1;
    public List<DisplayLayoutProfile> Profiles { get; set; } = [];
}

public sealed class DisplayLayoutProfile
{
    public string TopologyKey { get; set; } = "";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public double WorkspaceWidth { get; set; }
    public double WorkspaceHeight { get; set; }
    public List<FenceDisplayPlacement> Fences { get; set; } = [];
}

public sealed class FenceDisplayPlacement
{
    public string FenceId { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int PageIndex { get; set; }
}

public sealed record DisplayDescriptor(
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    uint DpiX,
    uint DpiY,
    bool IsPrimary);
