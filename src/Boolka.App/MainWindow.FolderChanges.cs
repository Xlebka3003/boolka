using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Boolka;

public partial class MainWindow
{
    private FileSystemWatcher? addonFolderWatcher;
    private DispatcherTimer? folderChangeTimer;
    private string? watchedAddonFolder;
    private long folderChangeVersion;
    private volatile bool folderWatcherFailed;
    private long scannedFolderVersion;

    private async Task<ScanResult> ScanInstalledFolderAsync(string folder)
    {
        var previousFolder = watchedAddonFolder;
        WatchAddonFolder(folder);
        var version = Interlocked.Read(ref folderChangeVersion);
        try
        {
            var result = await provider.ScanAsync(folder);
            scannedFolderVersion = version;
            UpdateFolderChangeButton();
            return result;
        }
        catch
        {
            if (previousFolder is not null && Directory.Exists(previousFolder)) WatchAddonFolder(previousFolder);
            else StopAddonFolderWatcher();
            throw;
        }
    }

    private void WatchAddonFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (addonFolderWatcher is not null && !folderWatcherFailed && string.Equals(folder, watchedAddonFolder, StringComparison.OrdinalIgnoreCase)) return;
        StopAddonFolderWatcher();
        watchedAddonFolder = folder;
        Interlocked.Exchange(ref folderChangeVersion, 0);
        scannedFolderVersion = 0;
        folderWatcherFailed = false;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16384
            };
            addonFolderWatcher = watcher;
            void Changed(object? sender, EventArgs args)
            {
                if (lifetime.IsCancellationRequested || !ReferenceEquals(addonFolderWatcher, watcher)) return;
                if (args is ErrorEventArgs) folderWatcherFailed = true;
                Interlocked.Increment(ref folderChangeVersion);
                try { Dispatcher.BeginInvoke(new Action(ScheduleFolderChangeSignal), DispatcherPriority.Background); }
                catch (InvalidOperationException) when (lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) { }
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += Changed;
            watcher.Error += Changed;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            addonFolderWatcher?.Dispose();
            addonFolderWatcher = null;
            AppLog.Error("folder.watch.failed", error);
        }
        UpdateFolderChangeButton();
    }

    private void ScheduleFolderChangeSignal()
    {
        if (lifetime.IsCancellationRequested) return;
        if (folderChangeTimer is null)
        {
            folderChangeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
            folderChangeTimer.Tick += (_, _) =>
            {
                // Одна подсветка после серии файловых событий; текущую операцию не перебиваем.
                if (busy) return;
                folderChangeTimer.Stop();
                UpdateFolderChangeButton();
            };
        }
        folderChangeTimer.Stop();
        folderChangeTimer.Start();
    }

    private void UpdateFolderChangeButton()
    {
        if (busy) return;
        var changed = Interlocked.Read(ref folderChangeVersion) > scannedFolderVersion;
        foreach (var button in new[] { CheckUpdatesButton, ReloadCatalogButton })
        {
            if (changed)
            {
                button.SetResourceReference(Control.BackgroundProperty, "StatusAttention");
                button.SetResourceReference(Control.BorderBrushProperty, "StatusAttention");
                button.SetResourceReference(Control.ForegroundProperty, "StatusAttentionText");
                button.FontWeight = FontWeights.SemiBold;
                button.ToolTip = UiText.Get("Папка AddOns изменилась. Нажмите, чтобы обновить список.");
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
                button.ClearValue(Control.ForegroundProperty);
                button.ClearValue(Control.FontWeightProperty);
                button.ClearValue(ToolTipProperty);
            }
        }
    }

    private void StopAddonFolderWatcher()
    {
        folderChangeTimer?.Stop();
        var watcher = addonFolderWatcher;
        addonFolderWatcher = null;
        watcher?.Dispose();
    }
}
