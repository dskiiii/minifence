namespace MiniFences.Models;

public sealed class FolderItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private string _fullPath = "";
    private string _toolTip = "";
    private System.Windows.Media.ImageSource? _icon;
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal)) return;
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (string.Equals(_fullPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _fullPath = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FullPath)));
        }
    }
    /// <summary>
    /// Friendly hover text. Shell namespace items use Explorer's description
    /// when available instead of exposing their internal parsing name.
    /// </summary>
    public string ToolTip
    {
        get => _toolTip;
        set
        {
            if (string.Equals(_toolTip, value, StringComparison.Ordinal)) return;
            _toolTip = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ToolTip)));
        }
    }
    public string Kind { get; init; } = "";
    public DateTime ModifiedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Size { get; init; }
    public string DisplaySize
    {
        get
        {
            if (string.Equals(Kind, "Folder", StringComparison.OrdinalIgnoreCase)) return "—";
            const double bytesPerKb = 1024d;
            const double bytesPerMb = 1024d * 1024d;
            const double bytesPerGb = 1024d * 1024d * 1024d;
            if (Size >= bytesPerGb) return $"{Size / bytesPerGb:0.##} GB";
            if (Size >= bytesPerMb) return $"{Size / bytesPerMb:0.#} MB";
            return $"{Size / bytesPerKb:0.#} KB";
        }
    }
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
