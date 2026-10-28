using System.IO;

namespace Boolka;

public partial class MainWindow
{
    private async Task<DependencyPlan> PrepareArchivesAsync(DependencyPlan plan, string directory,
        Dictionary<long, string> archives, CancellationTokenSource operation, bool discoverDependencies)
    {
        var number = 0;
        async Task<string> Download(Addon package)
        {
            operation.Token.ThrowIfCancellationRequested();
            EnsureStartupUpdateGameClosed();
            var current = ++number;
            var path = Path.Combine(directory, package.CatalogId!.Value + ".zip");
            var total = Math.Max(plan.Packages.Count, current);
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (activeDownload == operation && !installCommitting && !operation.IsCancellationRequested && current == number)
                    DownloadProgressChanged(current, total, package.Name, value.Bytes, value.Total);
            });
            DownloadProgressChanged(current, total, package.Name, 0, null);
            await downloader.DownloadAsync(package.CatalogId.Value, path, progress, operation.Token);
            return path;
        }

        if (!discoverDependencies)
        {
            // Импорт набора выполняет только уже согласованный план: без скрытого расширения.
            foreach (var package in plan.Packages)
                if (!archives.ContainsKey(package.CatalogId!.Value)) archives.Add(package.CatalogId.Value, await Download(package));
            return plan;
        }

        var ignored = (preferences.IgnoredIds ?? []).ToHashSet();
        var ignoredFolders = installed.Where(item => item.Comparison?.CatalogAddon?.CatalogId is long id && ignored.Contains(id))
            .Select(item => Path.GetFileName(item.Path.TrimEnd('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Allowed(Addon package) => !ignored.Contains(package.CatalogId!.Value) && !package.UnconfirmedInstallation
            && !package.PackageModules.Any(module => ignoredFolders.Contains(module.Path.Replace('\\', '/').Split('/')[0]));
        var expanded = await ArchiveDependencyPlanner.ExpandAsync(plan, catalog, installed, archives, Download, Allowed, operation.Token);
        try
        {
            await Task.Run(() =>
            {
                foreach (var package in expanded.Packages)
                    dependencyCache.Remember(package, archives[package.CatalogId!.Value]);
            }, operation.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { AppLog.Error("dependencies.cache.failed", error); }
        return expanded;
    }
}
