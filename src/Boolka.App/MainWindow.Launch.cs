using System.IO;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    private bool launching;

    private void ConfigureLaunch(object sender, RoutedEventArgs e) => OpenLaunchSettings();

    private bool OpenLaunchSettings(Window? owner = null)
    {
        if (busy || launching) return false;
        var dialog = new LaunchSettingsWindow(owner ?? this, preferences, currentFolder);
        if (dialog.ShowDialog() != true) return false;
        preferences = dialog.Selection;
        SavePreferences();
        return true;
    }

    private async void LaunchEso(object sender, RoutedEventArgs e)
    {
        if (busy || launching) return;
        if (!preferences.LaunchConfigured && !OpenLaunchSettings()) return;
        launching = true;
        SetBusy(true);
        LaunchButton.IsEnabled = false;
        try
        {
            var report = await LaunchSequence.RunAsync(preferences, currentFolder, new LaunchProcessRunner(),
                name => SetStatus(UiText.Get("Запускаем: ") + name), lifetime.Token);
            preferences = preferences with { HarvestPreparationStamp = report.HarvestStamp };
            SavePreferences();
            SetStatus(UiText.Get("Запуск ESO передан системе. Актуальность баз не подтверждена."));
            if (report.Warnings.Count > 0)
                SetStatus(StatusLabel.Text + " " + UiText.Get("Пропущено: ") + string.Join(" · ", report.Warnings), needsAttention: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (LaunchSequence.IsLaunchError(ex))
        {
            AppLog.Error("launch.failed", ex);
            SetStatus(UiText.Get("Не удалось запустить ESO:") + " " + UiError.Describe(ex), needsAttention: true);
        }
        finally
        {
            launching = false;
            SetBusy(false);
            LaunchButton.IsEnabled = true;
        }
    }
}
