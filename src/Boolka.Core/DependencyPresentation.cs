using System.IO;
using System.Numerics;

namespace Boolka;

internal sealed record DependencyPresentation(string Requirement, string Status, bool NeedsAttention);

internal static class DependencyPresenter
{
    private static IReadOnlyList<Addon> LocalAddons(Addon addon, IReadOnlyList<Addon> installed) => addon.Path.Length > 0
        ? new[] { addon }
        : installed.Where(item => item.Comparison?.CatalogAddon?.CatalogId == addon.CatalogId && addon.CatalogId is > 0).ToArray();

    internal static string Source(Addon addon, IReadOnlyList<Addon> installed, IReadOnlyList<CatalogModule>? checkedModules)
    {
        var local = LocalAddons(addon, installed);
        if (local.Count > 0) return local.All(item => item.LocalMetadataComplete)
            ? "По манифесту" : "По манифесту (прочитан не полностью)";
        return checkedModules is not null ? "Проверено по архиву" : "";
    }

    internal static IReadOnlyList<DependencyPresentation> Build(Addon addon, IReadOnlyList<Addon> installed, IReadOnlyList<CatalogModule>? checkedModules = null)
    {
        var local = LocalAddons(addon, installed);
        var hasLocal = local.Count > 0;
        var modules = hasLocal ? local.SelectMany(item => item.LocalModules).ToArray()
            : checkedModules ?? [];
        var internalModules = modules.Where(module => !module.IsLibrary).Select(module => module.Path.Replace('\\', '/').Split('/')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requirements = (hasLocal ? local.SelectMany(item => item.RequiredDependencies) : [])
            .Concat(modules.SelectMany(module => module.RequiredDependencies))
            .Where(requirement => !internalModules.Contains(requirement.Split('>')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return requirements.Select(requirement =>
        {
            var parts = requirement.Split(">=", StringSplitOptions.None);
            var name = parts[0];
            bool Suitable(string build) => parts.Length == 1 || parts.Length == 2
                && BigInteger.TryParse(parts[1], out var minimum) && BigInteger.TryParse(build, out var actual) && actual >= minimum;
            var separate = installed.Where(local => Path.GetFileName(local.Path.TrimEnd('\\', '/')).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var bundled = local.SelectMany(item => item.LocalModules)
                .Where(module => module.Path.Replace('\\', '/').Contains('/') && module.Path.Replace('\\', '/').Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Файлы в составе пакета показываем отдельно от самостоятельной установки библиотеки.
            if (separate.Any(item => Suitable(item.BuildVersion))) return new DependencyPresentation(requirement, "Установлена отдельно", false);
            if (bundled.Any(item => Suitable(item.BuildVersion))) return new DependencyPresentation(requirement, "Входит в состав мода", false);
            if (separate.Length > 0 || bundled.Length > 0) return new DependencyPresentation(requirement, "Нужно обновить", true);
            if (!addon.IsInstalled && modules.Any(module => module.Path.Replace('\\', '/').Contains('/') && module.Path.Replace('\\', '/').Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase) && Suitable(module.BuildVersion)))
                return new DependencyPresentation(requirement, "Входит в состав мода", false);
            return new DependencyPresentation(requirement, "Не установлена", true);
        }).ToArray();
    }
}
