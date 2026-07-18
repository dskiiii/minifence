using System.Windows;
using MiniFences.Services;

namespace MiniFences;

public partial class RenameFenceDialog : Window
{
    private readonly LocalizationService _loc = new();
    private readonly string _emptyMessageKey;

    public string InputText => TitleBox.Text.Trim();
    public string FenceTitle => InputText;

    public RenameFenceDialog(string currentTitle, LocalizationService localization)
        : this(currentTitle, localization, "RenameFence", "FenceTitle", "TitleCannotBeEmpty")
    {
    }

    public RenameFenceDialog(
        string currentText,
        LocalizationService localization,
        string titleKey,
        string labelKey,
        string emptyMessageKey)
    {
        InitializeComponent();
        _loc.Language = localization.Language;
        _emptyMessageKey = emptyMessageKey;
        Title = _loc.T(titleKey);
        TitleLabel.Text = _loc.T(labelKey);
        OkButton.Content = _loc.T("OK");
        CancelButton.Content = _loc.T("Cancel");
        TitleBox.Text = currentText;
        TitleBox.SelectAll();
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            System.Windows.MessageBox.Show(this, _loc.T(_emptyMessageKey), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
