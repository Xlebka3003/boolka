using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Boolka;

public partial class MainWindow
{
    private async void SaveDiagnostics(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Сохранить диагностический файл"), Filter = "ZIP (*.zip)|*.zip", DefaultExt = ".zip",
            AddExtension = true, OverwritePrompt = true, FileName = $"boolka-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(owner) != true) return;
        if (owner is SettingsWindow settings) settings.SetDiagnosticBusy(true);
        try
        {
            await AppLog.ExportAsync(dialog.FileName);
            StatusLabel.Text = UiText.Get("Диагностический файл сохранён. Просмотрите его перед отправкой разработчику.");
            if (owner is SettingsWindow settingsOwner) settingsOwner.SetDiagnosticStatus(StatusLabel.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Error("diagnostics.export.failed", ex);
            StatusLabel.Text = UiText.Get("Не удалось сохранить журнал: ") + ex.Message;
            if (owner is SettingsWindow settingsOwner) settingsOwner.SetDiagnosticStatus(StatusLabel.Text);
        }
        finally { if (owner is SettingsWindow settingsOwner) settingsOwner.SetDiagnosticBusy(false); }
    }

    private void ShowPreviousCrashNotice()
    {
        if (!AppLog.PreviousCrash) return;
        StatusLabel.Text += " · " + UiText.Get("В прошлый раз приложение завершилось с ошибкой. Сохраните журнал в настройках.");
        AppLog.AcknowledgeCrash();
    }
}
