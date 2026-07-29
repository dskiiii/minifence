namespace MiniFences.Models;

public sealed class FolderItem : System.ComponentModel.INotifyPropertyChanged
{
    private System.Windows.Media.ImageSource? _icon;
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Size { get; init; }
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
