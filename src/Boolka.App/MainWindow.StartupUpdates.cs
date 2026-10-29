using System.IO;

namespace Boolka;

public partial class MainWindow
{
    private readonly StartupAddonUpdater startupUpdater = new();
    private bool startupAutoUpdating;

    private async Task TryStartupAddonUpdateAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        try
        {
            var ready = !busy && catalogLoadedAt.HasValue && currentFolder is not null && Directory.Exists(currentFolder);
            var hasUpdates = installed.Any(addon => addon.HasUpdate && !addon.UpdatesIgnored);
            var result = await startupUpdater.RunAsync(preferences.AutoUpdateAddonsOnStartup, ready, hasUpdates,
                EsoLaunchService.IsGameRunning, async () =>
                {
                    startupAutoUpdating = true;
                    try { await ExecuteInstallationAsync(null, false, batch: true); }
                    finally { startupAutoUpdating = false; }
                }, lifetime.Token);
            if (result == StartupUpdateOutcome.GameRunning)
                SetStatus(UiText.Get("Автообновление пропущено: закройте ESO и нажмите «Обновить всё»."), needsAttention: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("startup.update.failed", error);
            SetStatus(UiText.Get("Не удалось выполнить автообновление: ") + UiError.Describe(error), needsAttention: true);
        }
    }

    private void EnsureStartupUpdateGameClosed()
    {
        if (startupAutoUpdating && EsoLaunchService.IsGameRunning())
            throw new IOException(UiText.Get("Автообновление остановлено: ESO запущена."));
    }
}
