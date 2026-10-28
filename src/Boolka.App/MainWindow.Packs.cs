using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;

namespace Boolka;

public partial class MainWindow
{
    private async Task<bool> LoadPackContextAsync()
    {
        if (busy || currentFolder is null || !Directory.Exists(currentFolder)) return false;
        SetBusy(true, UiText.Get("Проверяем состав набора и каталог ESOUI…"));
        try
        {
            installed = (await provider.ScanAsync(currentFolder)).Addons;
            catalog = await provider.GetCatalogAsync(lifetime.Token);
            catalogLoadedAt = DateTimeOffset.Now;
            ApplyComparisons();
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
        {
            AppLog.Error("ui.operation.failed", ex);
            if (!lifetime.IsCancellationRequested) StatusLabel.Text = UiText.Get("Не удалось открыть набор: ") + UiError.Describe(ex); return false;
        }
        finally { SetBusy(false); Refresh(); }
    }

    private async void ExportPack(object sender, RoutedEventArgs e)
    {
        if (!await LoadPackContextAsync()) return;
        var available = installed.Where(x => x.PreviewId is > 0).GroupBy(x => x.PreviewId!.Value)
            .Select(x => new PackAddon(x.Key, x.First().DisplayName)).OrderBy(x => x.Name).ToArray();
        var dialog = new PackExportWindow(this, available, installed.Count(x => x.PreviewId is not > 0));
        if (dialog.ShowDialog() != true || dialog.Result is not { } pack) return;
        SavePackToFile(pack);
    }

    private void SavePackToFile(AddonPack pack)
    {
        var save = new SaveFileDialog
        {
            Title = UiText.Get("Сохранить набор"),
            Filter = "boolka pack (*.boolka)|*.boolka",
            DefaultExt = ".boolka",
            AddExtension = true,
            FileName = "addons.boolka",
            OverwritePrompt = true
        };
        if (save.ShowDialog(this) != true) return;
        try { PackFile.Write(save.FileName, pack); StatusLabel.Text = UiText.Get("Набор сохранён: ") + save.FileName; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("ui.operation.failed", ex);
            StatusLabel.Text = UiText.Get("Не удалось сохранить набор: ") + UiError.Describe(ex);
        }
    }

    private async void ImportPack(object sender, RoutedEventArgs e)
    {
        if (busy || currentFolder is null) return;
        var open = new OpenFileDialog { Title = UiText.Get("Импорт набора"), Filter = "boolka pack (*.boolka)|*.boolka", Multiselect = false };
        if (open.ShowDialog(this) != true) return;
        try { await PreviewPackImportAsync(PackFile.Read(open.FileName)); }
        catch (OperationCanceledException ex)
        {
            if (!lifetime.IsCancellationRequested) StatusLabel.Text = UiError.Describe(ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("ui.operation.failed", ex);
            StatusLabel.Text = UiText.Get("Не удалось открыть набор: ") + UiError.Describe(ex);
        }
    }

    private async Task<string> DownloadPackArchiveAsync(Addon package, string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, package.CatalogId!.Value + ".zip");
        var progress = new Progress<DownloadProgress>(value => { if (busy && !installCommitting && !token.IsCancellationRequested) DownloadProgressChanged(1, 1, package.Name, value.Bytes, value.Total); });
        await downloader.DownloadAsync(package.CatalogId.Value, path, progress, token);
        return path;
    }

    private async Task PreviewPackImportAsync(AddonPack pack)
    {
        if (!await LoadPackContextAsync()) return;
        var rawPlan = PackPlanner.Build(pack, catalog, installed, (preferences.IgnoredIds ?? []).ToHashSet());
        var directory = Path.Combine(Path.GetTempPath(), "boolka-pack-review-" + Guid.NewGuid().ToString("N"));
        var archives = new Dictionary<long, string>();
        try
        {
            Directory.CreateDirectory(directory);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            PreparedPack prepared;
            BeginOperation();
            SetBusy(true, UiText.Get("Проверяем состав набора и каталог ESOUI…"));
            try
            {
                prepared = await PackArchivePlanner.PrepareAsync(pack, catalog, installed,
                    (preferences.IgnoredIds ?? []).ToHashSet(), archives,
                    package => DownloadPackArchiveAsync(package, directory, operation.Token), operation.Token);
                catalog = prepared.Catalog;
                ApplyComparisons();
            }
            finally { EndOperation(); SetBusy(false); Refresh(); }
            var plan = prepared.Plan;
            var approval = new PackImportApproval(pack, plan.Fingerprint, currentFolder!)
            { CatalogFingerprint = rawPlan.Fingerprint, Archives = archives };
            var review = new PackReviewWindow(this, pack, plan);
            if (review.ShowDialog() != true)
            {
                if (review.EditRequested)
                {
                    var choices = pack.Addons.Concat(installed.Where(x => x.PreviewId is > 0).Select(x => new PackAddon(x.PreviewId!.Value, x.DisplayName)))
                        .DistinctBy(x => x.Id).OrderBy(x => x.Name).ToArray();
                    var editor = new PackExportWindow(this, choices, 0, pack);
                    if (editor.ShowDialog() == true && editor.Result is { } edited) SavePackToFile(edited);
                }
                return;
            }
            await ExecuteInstallationAsync(null, false, batch: true, packImport: approval);
        }
        finally
        {
            // Удаляем только собственные временные ZIP; папка игры здесь не затрагивается.
            foreach (var file in archives.Values)
                try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(directory, false); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
