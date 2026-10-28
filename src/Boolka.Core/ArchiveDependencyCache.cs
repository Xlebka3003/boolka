using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boolka;

/// <summary>Кэш сведений для отображения. Установщик по-прежнему проверяет реальные архивы.</summary>
internal sealed class ArchiveDependencyCache(string path)
{
    private sealed record Cached(long Id, string Version, string Checksum, CatalogModule[] Modules, int Schema = 0);
    private List<Cached>? entries;

    internal IReadOnlyList<CatalogModule>? Find(Addon? addon)
    {
        if (addon is null || addon.CatalogId is not > 0 || addon.CatalogChecksum.Length != 32) return null;
        var item = Read().LastOrDefault(item => item.Schema == 1 && item.Id == addon.CatalogId && item.Version == addon.Version
            && item.Checksum.Equals(addon.CatalogChecksum, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Modules is null || item.Modules.Length != addon.PackageModules.Count) return null;
        // Локальный кэш не должен подменять пути и сборки, заявленные свежим каталогом.
        return item.Modules.All(module => module is { Path: not null, BuildVersion: not null, RequiredDependencies: not null } && DependencyResolver.IsSafePackagePath(module.Path)
            && module.RequiredDependencies is not null && module.RequiredDependencies.Count <= 1000
            && module.RequiredDependencies.All(requirement => requirement is { Length: > 0 and <= 256 })
            && addon.PackageModules.Count(actual => actual.Path == module.Path && actual.BuildVersion == module.BuildVersion) == 1)
            ? item.Modules : null;
    }

    internal void Remember(Addon addon, string archive)
    {
        if (!addon.ManifestDependenciesVerified || addon.CatalogId is not > 0 || addon.CatalogChecksum.Length != 32) return;
        using var file = File.OpenRead(archive);
        var checksum = Convert.ToHexString(MD5.HashData(file));
        if (!checksum.Equals(addon.CatalogChecksum, StringComparison.OrdinalIgnoreCase)) return;
        var list = Read().Where(item => item.Id != addon.CatalogId).Append(new Cached(addon.CatalogId.Value,
            addon.Version, checksum, addon.PackageModules.ToArray(), 1)).TakeLast(500).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(list);
        if (bytes.Length > 2 * 1024 * 1024) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
        entries = list;
    }

    private List<Cached> Read()
    {
        if (entries is not null) return entries;
        try
        {
            entries = File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024
                ? (JsonSerializer.Deserialize<List<Cached>>(File.ReadAllText(path)) ?? []).Where(item => item is { Checksum.Length: 32, Version: not null, Modules: not null }).Take(500).ToList() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { entries = []; }
        return entries;
    }
}
