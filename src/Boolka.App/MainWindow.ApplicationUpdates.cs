using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Boolka;

public partial class MainWindow
{
    private readonly ApplicationReleaseClient applicationReleases = new();
    private ApplicationRelease? applicationRelease;
    private static Version ApplicationVersion
    {
        get
        {
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            return new(version.Major, version.Minor, version.Build);
        }
    }

    private async Task MonitorApplicationUpdatesAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                applicationRelease = await applicationReleases.CheckAsync(ApplicationVersion, lifetime.Token);
                if (!lifetime.IsCancellationRequested) UpdateApplicationButton();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Проверка фоновая: ни диалогов, ни сообщений поверх текущей операции.
                AppLog.Error("app.update.check.failed", error);
            }
            try { await Task.Delay(TimeSpan.FromHours(1), lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void UpdateApplicationButton()
    {
        var available = applicationRelease is not null;
        AppUpdateButton.IsEnabled = available && !busy;
        AppUpdateButton.Foreground = available ? new SolidColorBrush(Color.FromRgb(80, 250, 123)) : ThemeManager.Brush("#9AA3B5");
        var hint = available
            ? UiText.Format($"Доступна boolka {applicationRelease!.Version}. Нажмите, чтобы скачать и обновить.")
            : UiText.Get("Проверка обновлений boolka выполняется автоматически.");
        AppUpdateButton.ToolTip = hint;
        AutomationProperties.SetHelpText(AppUpdateButton, hint);
    }

    private async void UpdateApplication(object sender, RoutedEventArgs e)
    {
        if (busy || applicationRelease is null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeDownload = operation;
        SetBusy(true, UiText.Get("Проверяем обновление boolka…"));
        BeginOperation();
        try
        {
            Program.RequireUnelevatedUpdate();
            var release = await applicationReleases.CheckAsync(ApplicationVersion, operation.Token);
            applicationRelease = release;
            if (release is null)
            {
                StatusLabel.Text = UiText.Get("Проверка обновлений boolka выполняется автоматически.");
                return;
            }
            var executable = Environment.ProcessPath ?? throw new IOException("Не найден исполняемый файл приложения.");
            if (!string.Equals(Path.GetFileName(executable), "boolka.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Обновление доступно только для автономной сборки boolka.exe.");
            var targetProbe = Path.Combine(Path.GetDirectoryName(executable)!, ".boolka-write-" + Guid.NewGuid().ToString("N"));
            ApplicationUpdatePackage.CheckPath(targetProbe);
            using (new FileStream(targetProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var workspace = ApplicationUpdatePackage.CreateWorkspace();
            var archive = Path.Combine(workspace, "update.zip");
            var progress = new Progress<long>(bytes =>
            {
                if (operation.IsCancellationRequested) return;
                OperationProgress.IsIndeterminate = false;
                OperationProgress.Value = bytes * 100.0 / release.Size;
                StatusLabel.Text = UiText.Format($"Скачиваем boolka: {bytes / 1048576.0:0.0} из {release.Size / 1048576.0:0.0} МБ");
            });
            await applicationReleases.DownloadAsync(release, archive, progress, operation.Token);
            StatusLabel.Text = UiText.Get("Проверяем файлы обновления. Приложение перезапустится автоматически.");
            OperationProgress.IsIndeterminate = true;
            using var parent = Process.GetCurrentProcess();
            var target = Path.GetDirectoryName(executable)!;
            var parentStart = parent.StartTime.ToUniversalTime().Ticks;
            await Task.Run(() => ApplicationUpdatePackage.Prepare(archive, workspace, target, release.Version, Environment.ProcessId, parentStart, release.Sha256), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var helper = Path.Combine(workspace, "helper.exe");
            await Task.Run(() => File.Copy(executable, helper, false), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = workspace, CreateNoWindow = true };
            start.ArgumentList.Add("--apply-app-update");
            start.ArgumentList.Add(Path.Combine(workspace, "plan.json"));
            using var worker = Process.Start(start) ?? throw new IOException("Не удалось запустить помощник обновления.");
            var ready = Path.Combine(workspace, "ready");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (worker.HasExited || watch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new IOException("Помощник обновления не готов. Приложение остаётся открытым.");
                await Task.Delay(100, operation.Token);
            }
            // Помощник начнёт замену лишь после завершения этого процесса.
            ApplicationUpdatePackage.WriteNewText(Path.Combine(workspace, "approved"), "approved");
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) StatusLabel.Text = UiText.Get("Обновление boolka отменено. Файлы приложения не изменены.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Error("app.update.failed", error);
            if (!lifetime.IsCancellationRequested) StatusLabel.Text = UiText.Get("Не удалось обновить boolka: ") + UiError.Describe(error);
        }
        finally
        {
            activeDownload = null;
            EndOperation();
            SetBusy(false);
        }
    }
}
