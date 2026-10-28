namespace Boolka;

internal static class AddonRequirements
{
    internal static IReadOnlyList<string> For(Addon addon)
    {
        var required = addon.RequiredDependencies.AsEnumerable();
        if (addon.Path.Length > 0)
        {
            // Требования установленной версии берём из её файлов, а не из более нового выпуска каталога.
            var own = addon.LocalModules.Select(module => module.Path.Replace('\\', '/').Split('/')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return required.Concat(addon.LocalModules.SelectMany(module => module.RequiredDependencies))
                .Where(requirement => !own.Contains(requirement.Split('>')[0]))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (addon.DisplayCatalog is { DependencyMetadataValid: true } remote)
        {
            // Внешние зависимости всего пакета важны и для корневого манифеста-заглушки.
            var ownModules = remote.PackageModules.Select(module => module.Path.Replace('\\', '/').Split('/')[^1])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            required = required.Concat(remote.PackageModules.SelectMany(module => module.RequiredDependencies))
                .Where(requirement => !ownModules.Contains(requirement.Split('>')[0]));
        }
        return required.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
