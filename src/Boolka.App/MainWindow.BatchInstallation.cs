using System.IO;
using System.Net.Http;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    private async Task InstallBatchIndividuallyAsync(DependencyPlan batchPlan, IReadOnlyList<string> initiallySkipped,
        bool librariesOnly, string target, string directory, Dictionary<long, string> archives, CancellationTokenSource operation, PackImportApproval? packImport = null)
    {
        var completed = new HashSet<long>();
        var failed = new HashSet<long>();
        var allowed = batchPlan.Packages.Select(x => x.CatalogId!.Value).ToHashSet();
        var errors = new List<string>();
        var added = 0;
        var updated = 0;
        var position = 0;
        foreach (var original in batchPlan.Packages)
        {
            operation.Token.ThrowIfCancellationRequested();
            EnsureStartupUpdateGameClosed();
            position++;
            var id = original.CatalogId!.Value;
            if (completed.Contains(id)) continue;
            try
            {
                var root = catalog.Single(x => x.CatalogId == id);
                var unit = ArchiveDependencyPlanner.Build(root, catalog, installed);
                if (!unit.IsReady) throw new InvalidDataException(string.Join("; ", unit.Issues));
                if (unit.Packages.Any(x => !allowed.Contains(x.CatalogId!.Value) || failed.Contains(x.CatalogId.Value)))
                    throw new InvalidDataException(UiText.Get("Обязательная библиотека не установлена. Проект пропущен."));

                unit = await PrepareArchivesAsync(unit, directory, archives, operation, discoverDependencies: packImport is null);
                if (unit.Packages.Any(package => failed.Contains(package.CatalogId!.Value)))
                    throw new InvalidDataException(UiText.Get("Обязательная библиотека не установлена. Проект пропущен."));

                installCommitting = true;
                OperationProgress.IsIndeterminate = true;
                SetStatus(UiText.Format($"Устанавливаем {position} из {batchPlan.Packages.Count} · {root.Name}"));
                var newCount = unit.Packages.Count(x => !x.IsInstalled);
                // Один мод с обязательными библиотеками — отдельная транзакция с собственным откатом.
                EnsureStartupUpdateGameClosed();
                var result = await new SafeInstaller().InstallAsync(unit, archives, target, operation.Token, allowReplacement: packImport is null);
                lastBackupDirectory = result.BackupDirectory;
                added += newCount;
                updated += unit.Packages.Count - newCount;
                foreach (var package in unit.Packages) completed.Add(package.CatalogId!.Value);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException
                or System.Security.Cryptography.CryptographicException or InvalidOperationException
                || ex is OperationCanceledException && !operation.IsCancellationRequested)
            {
                AppLog.Error("install.package.failed", ex);
                failed.Add(id);
                errors.Add(original.Name + ": " + UiError.Describe(ex));
            }
            finally
            {
                installCommitting = false;
            }

            // Следующий план строится по фактическому результату предыдущей транзакции.
            // При ошибке чтения прекращаем пакет, не продолжая со старой моделью папки.
            installed = (await ScanInstalledFolderAsync(target)).Addons;
            ApplyComparisons();
        }

        var summary = UiText.Format($"Готово · Установлено: {added} · Обновлено: {updated} · Пропущено: {initiallySkipped.Count + failed.Count}");
        if (errors.Count > 0)
        {
            OperationFailed(new InvalidDataException(string.Join("; ", errors)),
                packImport is null ? () => ExecuteInstallationAsync(null, librariesOnly, batch: true) : () => PreviewPackImportAsync(packImport.Pack));
            summary += " · " + string.Join("; ", errors);
        }
        SetStatus(summary, needsAttention: errors.Count > 0 || initiallySkipped.Count > 0);
    }
}
