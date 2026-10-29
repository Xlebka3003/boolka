using System.IO;
using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed record DependencyPlan(Addon Root, IReadOnlyList<Addon> Packages, IReadOnlyList<string> Issues)
{
    public bool IsReady => Issues.Count == 0;
}

public static class DependencyResolver
{
    public static bool IsSafePackagePath(string path) => path.Length is > 0 and <= 512
        && !path.Contains(':') && !path.Contains('\0')
        && path.Replace('\\', '/').Split('/') is { Length: <= 16 } segments
        && segments.All(AddonMatcher.IsTopLevelFolder);

    private static string Leaf(string path) => path.Replace('\\', '/').Split('/')[^1];
    private static bool Satisfies(string build, string? minimum) => minimum is null ||
        build.Length is > 0 and <= 128 && build.All(char.IsAsciiDigit) && BigInteger.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= BigInteger.Parse(minimum, CultureInfo.InvariantCulture);

    public static DependencyPlan Build(Addon root, IReadOnlyList<Addon> catalog, IReadOnlyList<Addon> installed, bool includeRoot = true)
    {
        var issues = new List<string>(); var ordered = new List<Addon>();
        var selected = new Dictionary<long, Addon>(); var localVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = catalog.Where(x => x.CatalogId is > 0).SelectMany(x => x.CatalogFolders.Select(f => (f.Folder, Package: x)))
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Select(y => y.Package).DistinctBy(y => y.CatalogId).ToList(), StringComparer.OrdinalIgnoreCase);
        var local = installed.Where(x => x.Path.Length > 0).GroupBy(x => Path.GetFileName(x.Path.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var edges = 0;

        void Require(string dependency, int depth)
        {
            if (++edges > 2000 || depth > 64) { issues.Add("Слишком большой или глубокий список зависимостей."); return; }
            if (dependency.Length > 256) { issues.Add("Слишком длинная запись зависимости."); return; }
            var match = Regex.Match(dependency, @"^([^\s<>=]+)(?:>=([0-9]{1,128}))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !AddonMatcher.IsTopLevelFolder(match.Groups[1].Value)) { issues.Add($"Не поддерживается зависимость: {dependency}"); return; }
            var name = match.Groups[1].Value; var minimum = match.Groups[2].Success ? match.Groups[2].Value : null;
            if (selected.Values.SelectMany(x => x.PackageModules).Any(m => Leaf(m.Path).Equals(name, StringComparison.OrdinalIgnoreCase) && Satisfies(m.BuildVersion, minimum))) return;
            if (local.TryGetValue(name, out var present) && present.Count == 1 && Satisfies(present[0].BuildVersion, minimum))
            {
                if (localVisited.Add(name)) foreach (var child in present[0].RequiredDependencies) Require(child, depth + 1);
                return;
            }
            if (!owners.TryGetValue(name, out var candidates)) { issues.Add($"Не найден обязательный модуль {dependency}."); return; }
            var dedicated = candidates.Where(p => p.CatalogFolders.Count == 1 && p.PackageModules.Any(m => m.Path.Equals(name, StringComparison.OrdinalIgnoreCase) && m.IsLibrary)).ToList();
            var choices = dedicated.Count > 0 ? dedicated : candidates;
            // Сначала исключаем проекты, не обеспечивающие минимальную сборку.
            // Два владельца одного имени ещё не означают неоднозначность подходящей версии.
            choices = choices.Where(package => package.PackageModules.Where(module => module.Path.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()
                is { Length: 1 } supplied && Satisfies(supplied[0].BuildVersion, minimum)).ToList();
            if (choices.Count == 0) { issues.Add($"Каталог не подтверждает требуемую сборку {dependency}."); return; }
            if (choices.Count != 1) { issues.Add($"Неоднозначный проект для {name}: {choices.Count} кандидатов."); return; }
            var package = choices[0];
            var modules = package.PackageModules.Where(m => m.Path.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (modules.Count != 1 || !Satisfies(modules[0].BuildVersion, minimum)) { issues.Add($"Каталог не подтверждает требуемую сборку {dependency}."); return; }
            if (selected.ContainsKey(package.CatalogId!.Value)) { issues.Add($"Выбранный пакет не обеспечивает {dependency}."); return; }
            AddPackage(package, depth + 1);
        }

        void AddPackage(Addon package, int depth)
        {
            if (package.CatalogId is not > 0 || package.PackageModules.Count == 0 || !package.DependencyMetadataValid) { issues.Add($"Нет полных сведений о модулях пакета {package.Name}."); return; }
            if (selected.ContainsKey(package.CatalogId.Value)) return;
            if (selected.Count >= 100 || depth > 64) { issues.Add("Слишком много пакетов зависимостей."); return; }
            if (package.PackageModules.Any(x => !IsSafePackagePath(x.Path))) { issues.Add($"Некорректные пути модулей в {package.Name}."); return; }
            if (package.PackageModules.GroupBy(x => x.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            { issues.Add($"Неоднозначные модули в пакете {package.Name}."); return; }
            selected.Add(package.CatalogId.Value, package);
            foreach (var module in package.PackageModules)
                foreach (var requirement in module.RequiredDependencies) Require(requirement, depth + 1);
            ordered.Add(package);
        }

        AddPackage(root, 0);
        return new(root, ordered.Where(x => includeRoot || x.CatalogId != root.CatalogId).ToList(), issues.Distinct().ToList());
    }
}
