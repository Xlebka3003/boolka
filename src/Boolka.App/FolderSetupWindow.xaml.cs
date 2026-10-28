using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Boolka;

public partial class FolderSetupWindow : Window
{
    public string SelectedFolder { get; private set; } = "";

    public FolderSetupWindow(string initialFolder, string? warning = null)
    {
        InitializeComponent();
        DialogChrome.Apply(this);
        FolderPathBox.Text = initialFolder;
        ErrorLabel.Text = warning is null ? "" : UiText.Get(warning);
    }

    public static string? ExistingParent(string path)
    {
        try
        {
            while (!string.IsNullOrWhiteSpace(path))
            {
                if (Directory.Exists(path)) return path;
                path = Path.GetDirectoryName(path) ?? "";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return null;
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("Выберите папку ESO: live\\AddOns"), Multiselect = false };
        var initial = ExistingParent(FolderPathBox.Text.Trim());
        if (initial is not null) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) == true) FolderPathBox.Text = dialog.FolderName;
    }

    private void PathChanged(object sender, TextChangedEventArgs e) { if (ErrorLabel is not null) ErrorLabel.Text = ""; }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        var input = FolderPathBox.Text.Trim();
        try
        {
            if (!Path.IsPathFullyQualified(input) || !Directory.Exists(input))
            {
                ErrorLabel.Text = UiText.Get("Папка не найдена. Укажите существующую папку AddOns или выберите её через «Обзор».");
                return;
            }
            SelectedFolder = Path.GetFullPath(input);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { ErrorLabel.Text = UiText.Get("Не удалось открыть этот путь. Проверьте папку и права доступа."); }
    }
}
