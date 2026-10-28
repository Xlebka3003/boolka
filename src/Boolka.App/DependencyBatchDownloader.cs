using System.IO;

namespace Boolka;

public sealed record BatchDownloadProgress(string Name, int Index, int Count, long Bytes, long? Total);
public sealed record BatchDownloadResult(IReadOnlyList<string> Files, string? LibraryDirectory);

public sealed class DependencyBatchDownloader
{
    private readonly ArchiveDownloader downloader;
    public DependencyBatchDownloader(ArchiveDownloader? downloader = null) => this.downloader = downloader ?? new();

    public async Task<BatchDownloadResult> DownloadAsync(DependencyPlan plan, string directory, string? rootDestination,
        IProgress<BatchDownloadProgress>? progress, CancellationToken token)
    {
        if (!plan.IsReady) throw new InvalidDataException("Зависимости не определены: " + string.Join("; ", plan.Issues));
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Папка сохранения не найдена.");
        if (rootDestination is not null)
        {
            rootDestination = Path.GetFullPath(rootDestination);
            if (!Path.GetDirectoryName(rootDestination)!.Equals(directory, StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(rootDestination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("ZIP аддона должен находиться в выбранной папке сохранения.");
            if (!plan.Packages.Any(x => x.CatalogId == plan.Root.CatalogId)) throw new InvalidDataException("В плане нет основного архива.");
        }
        var packages = plan.Packages.Where(x => rootDestination is not null || x.CatalogId != plan.Root.CatalogId).ToList();
        if (packages.Count == 0) return new(Array.Empty<string>(), null);
        if (packages.Count > 100 || packages.Any(x => x.CatalogId is not > 0) || packages.DistinctBy(x => x.CatalogId).Count() != packages.Count)
            throw new InvalidDataException("Некорректный список архивов.");
        var key = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(directory, ".boolka-dependencies-" + key);
        var stageLibraries = Path.Combine(stage, "libraries");
        var finalLibraries = Path.Combine(directory, "boolka-libraries-" + key[..12]);
        var stagedFiles = new List<string>(); var publishedFiles = new List<string>();
        var committed = false; var librariesPublished = false;
        try
        {
            Directory.CreateDirectory(stageLibraries);
            for (var i = 0; i < packages.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var package = packages[i];
                var isRoot = rootDestination is not null && package.CatalogId == plan.Root.CatalogId;
                var path = isRoot ? Path.Combine(stage, "root.zip") : Path.Combine(stageLibraries, DownloadMetadata.SuggestedFileName(package.Name, package.Version, package.CatalogId!.Value));
                stagedFiles.Add(path);
                var index = i + 1;
                var relay = new Relay(value => progress?.Report(new(package.Name, index, packages.Count, value.Bytes, value.Total)));
                await downloader.DownloadAsync(package.CatalogId!.Value, path, relay, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var libraries = packages.Where(x => rootDestination is null || x.CatalogId != plan.Root.CatalogId).ToList();
            if (libraries.Count > 0)
            {
                Directory.Move(stageLibraries, finalLibraries);
                librariesPublished = true;
                publishedFiles.AddRange(libraries.Select(x => Path.Combine(finalLibraries, DownloadMetadata.SuggestedFileName(x.Name, x.Version, x.CatalogId!.Value))));
            }
            if (rootDestination is not null)
            {
                File.Move(Path.Combine(stage, "root.zip"), rootDestination, overwrite: true);
                publishedFiles.Add(rootDestination);
            }
            committed = true;
            return new(publishedFiles, libraries.Count > 0 ? finalLibraries : null);
        }
        finally
        {
            // Only delete exact files and empty folders created by this operation;
            // never recursively traverse a user-selected directory.
            foreach (var file in stagedFiles) TryDeleteFile(file);
            if (!committed && librariesPublished)
            {
                foreach (var file in publishedFiles) TryDeleteFile(file);
                TryDeleteDirectory(finalLibraries);
            }
            TryDeleteDirectory(stageLibraries); TryDeleteDirectory(stage);
        }
    }

    private sealed class Relay(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
