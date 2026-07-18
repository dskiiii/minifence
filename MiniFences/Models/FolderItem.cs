namespace MiniFences.Models;

public sealed class FolderItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Size { get; init; }
    public System.Windows.Media.ImageSource? Icon { get; init; }
}
