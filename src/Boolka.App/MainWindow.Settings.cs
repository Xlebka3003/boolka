using System.IO;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SettingsWindow? dialog = null;
        dialog = new SettingsWindow(this, preferences, ApplyPresentation, () =>
        {
            if (currentFolder is null) return;
            var target = currentFolder;
            var backups = new BackupsWindow(dialog!, target, value =>
            {
                installCommitting = value;
                SetBusy(value);
            }, async () =>
            {
                try
                {
                    installed = (await provider.ScanAsync(target)).Addons;
                    ApplyComparisons();
                    Refresh();
                    StatusLabel.Text = catalogMode ? CatalogStatus() : InstalledStatus();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    AppLog.Error("ui.operation.failed", ex);
                    StatusLabel.Text = UiText.Get("Копия восстановлена, но не удалось пересканировать папку: ") + UiError.Describe(ex);
                }
            });
            backups.ShowDialog();
        }, currentFolder is not null, () => SaveDiagnostics(dialog!), () => OpenLaunchSettings(dialog), enabled =>
        {
            preferences = preferences with { AutoUpdateAddonsOnStartup = enabled };
            SavePreferences();
        });
        dialog.ShowDialog();
    }

    private void ApplyAppearance(string language, string theme) => ApplyPresentation(language, theme, preferences.PreviewMode);

    private void ApplyPresentation(string language, string theme, string previewMode)
    {
        preferences = preferences with { Language = language, Theme = theme, SquareCorners = true, PreviewMode = previewMode == "icons" ? "icons" : "images" };
        UiResources.Apply(language);
        UpdateCategoryOptions();
        ThemeManager.Apply(theme);
        CategoryIcons.ApplyMode(preferences.PreviewMode);
        UpdateViewButtons();
        ApplyCollectionScale();
        ApplyComparisons();
        Refresh();
        StatusLabel.Text = catalogMode ? CatalogStatus() : InstalledStatus();
        SavePreferences();
    }
}
