using System.IO;
using System.IO.Compression;

namespace Boolka;

/// <summary>Уточняет обязательные зависимости по скачанным манифестам, не извлекая файлы в AddOns.</summary>
internal static class ArchiveDependencyPlanner
{
    // Каталог нужен для поиска владельцев модулей. Требования берём только из проверенного
    // ZIP; для ремонта без замены корня сохраняем реальные локальные требования.
    internal static DependencyPlan Build(Addon root, IReadOnlyList<Addon> catalog,
        IReadOnlyList<Addon> installed, bool includeRoot = true)
    {
        var source = catalog.Select(WithoutUnverifiedRequirements).ToArray();
        return DependencyResolver.Build(includeRoot ? WithoutUnverifiedRequirements(root) : root,
            source, installed, includeRoot);
    }

    private static Addon WithoutUnverifiedRequirements(Addon addon) => addon.ManifestDependenciesVerified
        ? addon : addon with
        {
            RequiredDependencies = [],
            PackageModules = addon.PackageModules.Select(module => module with { RequiredDependencies = [] }).ToArray()
        };

    internal static async Task<DependencyPlan> ExpandAsync(DependencyPlan initial, IReadOnlyList<Addon> catalog,
        IReadOnlyList<Addon> installed, IDictionary<long, string> archives, Func<Addon, Task<string>> download,
        Func<Addon, bool> allowed, CancellationToken token)
    {
        var metadata = catalog.ToDictionary(item => item.CatalogId!.Value);
        var inspected = new HashSet<long>();
        var includeRoot = initial.Packages.Any(item => item.CatalogId == initial.Root.CatalogId);
        var root = initial.Root;
        var plan = Build(root, catalog, installed, includeRoot);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!plan.IsReady) throw new InvalidDataException(string.Join("; ", plan.Issues));
            foreach (var package in plan.Packages)
                if (!allowed(package)) throw new InvalidDataException(UiText.Get("Обязательный пакет исключён из установки: ") + package.Name);
            var next = plan.Packages.FirstOrDefault(item => !inspected.Contains(item.CatalogId!.Value));
            if (next is null) return plan;
            if (inspected.Count >= 100 || archives.Count > 100) throw new InvalidDataException(UiText.Get("Слишком много пакетов зависимостей."));
            var id = next.CatalogId!.Value;
            if (!archives.TryGetValue(id, out var path))
            {
                path = await download(next);
                archives.Add(id, path);
            }
            var updated = await Task.Run(() => Inspect(next, path, token), token);
            metadata[id] = updated;
            inspected.Add(id);
            if (id == root.CatalogId) root = updated;
            plan = Build(root, metadata.Values.ToArray(), installed, includeRoot);
        }
    }

    internal static Addon Inspect(Addon package, string archive, CancellationToken token)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ArchiveDownloader.MaxArchiveBytes) throw new InvalidDataException("ZIP size limit exceeded.");
        ArchiveDownloader.ValidateDirectoryAsync(stream, token).GetAwaiter().GetResult();
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 50000 || package.PackageModules.Count > 1000) throw new InvalidDataException("ZIP entry limit exceeded.");
        var entries = zip.Entries.GroupBy(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var modules = new List<CatalogModule>();
        long total = 0;
        foreach (var module in package.PackageModules)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeInstaller.IsSafeRelativePath(module.Path)) throw new InvalidDataException("Unsafe module path.");
            var modulePath = module.Path.Replace('\\', '/');
            var name = modulePath.Split('/')[^1];
            var matches = entries.GetValueOrDefault(modulePath + "/" + name + ".txt")
                ?? entries.GetValueOrDefault(modulePath + "/" + name + ".addon");
            if (matches is not { Length: 1 }) throw new InvalidDataException(UiText.Get("Не найден единственный манифест: ") + modulePath);
            var entry = matches[0];
            total += entry.Length;
            if (entry.Length > 1024 * 1024 || total > 16 * 1024 * 1024) throw new InvalidDataException("Manifest size limit exceeded.");
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is not (0 or 0x8000) || (entry.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("Linked manifest is not allowed.");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = source.Read(chunk)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (buffer.Length + count > entry.Length || buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Manifest size mismatch.");
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length != entry.Length) throw new InvalidDataException("Incomplete manifest.");
            buffer.Position = 0;
            using var reader = new StreamReader(buffer);
            var dependencies = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.StartsWith("##") || !line.Contains(':')) continue;
                var field = line[2..].Split(':', 2);
                if (ManifestFields.Normalize(field[0]).Equals("DependsOn", StringComparison.OrdinalIgnoreCase))
                    dependencies.AddRange(field[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (field[0].Trim().Equals("AddOnVersion", StringComparison.OrdinalIgnoreCase)
                    && module.BuildVersion.Length > 0 && field[1].Trim() != module.BuildVersion)
                    throw new InvalidDataException(UiText.Get("Сборка модуля отличается от каталога: ") + modulePath);
            }
            // Манифест текущего ZIP заменяет прежние требования, включая удалённые автором.
            modules.Add(module with { RequiredDependencies = dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
        }
        return package with { PackageModules = modules, RequiredDependencies = modules.SelectMany(module => module.RequiredDependencies).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ManifestDependenciesVerified = true };
    }
}
