using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed record BatchPlanResult(DependencyPlan? Plan, IReadOnlyList<string> Skipped);

public static class BatchPlanBuilder
{
    public static BatchPlanResult Build(IReadOnlyList<Addon> installed, IReadOnlyList<Addon> catalog,
        bool librariesOnly, IReadOnlySet<long> ignored)
    {
        var skipped = new List<string>();
        var issues = new List<string>();
        var packages = new Dictionary<long, Addon>();
        var ignoredFolders = installed.Where(x => x.Comparison?.CatalogAddon?.CatalogId is long id && ignored.Contains(id))
            .Select(x => Folder(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = installed.Where(x => x.Comparison?.CatalogAddon?.CatalogId is > 0)
            .GroupBy(x => x.Comparison!.CatalogAddon!.CatalogId!.Value).OrderBy(x => x.Key);
        foreach (var group in groups)
        {
            var actual = group.ToList();
            var health = librariesOnly ? actual.Select(x => DependencyHealth.Evaluate(x, installed)).ToList() : null;
            if (librariesOnly ? !health!.Any(x => x.HasProblems || x.Unknown.Count > 0) : !actual.Any(x => x.Comparison?.Status == UpdateStatus.UpdateAvailable)) continue;
            var label = actual[0].Comparison!.CatalogAddon!.Name;
            if (ignored.Contains(group.Key)) { skipped.Add(label + " — проект исключён пользователем."); continue; }
            var candidates = catalog.Where(x => x.CatalogId == group.Key).ToList();
            if (candidates.Count != 1) { skipped.Add(label + " — проект не найден однозначно в свежем каталоге."); continue; }
            var root = candidates[0];
            if (librariesOnly)
            {
                if (health!.Any(x => x.Unknown.Count > 0) || actual.Any(x => !x.LocalMetadataComplete))
                { skipped.Add(label + " — локальные зависимости требуют ручной проверки."); continue; }
                root = root with
                {
                    PackageModules = actual.Select(x => new CatalogModule(Folder(x.Path), x.BuildVersion, x.RequiredDependencies)).ToList(),
                    DependencyMetadataValid = true
                };
            }
            var plan = DependencyResolver.Build(root, catalog, installed, includeRoot: !librariesOnly);
            if (!plan.IsReady) { skipped.Add(label + " — " + string.Join("; ", plan.Issues)); continue; }
            if (plan.Packages.Any(x => x.CatalogId is long id && ignored.Contains(id)))
            { skipped.Add(label + " — обязательный пакет исключён пользователем."); continue; }
            if (plan.Packages.SelectMany(x => x.PackageModules).Any(m => ignoredFolders.Contains(m.Path.Replace('\\', '/').Split('/')[0])))
            { skipped.Add(label + " — пакет затрагивает папку исключённого проекта."); continue; }
            foreach (var package in plan.Packages)
            {
                if (package.CatalogId is not > 0) { issues.Add("В общем плане нет идентификатора пакета."); continue; }
                var id = package.CatalogId.Value;
                if (packages.TryGetValue(id, out var prior) && !Equivalent(prior, package))
                    issues.Add($"Различаются сведения о проекте {id} в отдельных планах.");
                else packages.TryAdd(id, package);
            }
        }
        foreach (var addon in installed.Where(x => librariesOnly
                     ? DependencyHealth.Evaluate(x, installed) is var report && (report.HasProblems || report.Unknown.Count > 0) && x.Comparison?.CatalogAddon?.CatalogId is not > 0
                     : x.Comparison?.Status == UpdateStatus.UpdateAvailable && x.Comparison.CatalogAddon?.CatalogId is not > 0))
            skipped.Add(addon.Name + " — нет подтверждённого проекта ESOUI.");
        if (packages.Count == 0) return new(null, skipped.Distinct().ToArray());
        if (packages.Count > 100) issues.Add("В общей установке больше 100 пакетов.");

        var owners = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.Values)
            foreach (var module in package.PackageModules)
            {
                if (!DependencyResolver.IsSafePackagePath(module.Path)) { issues.Add("Некорректный путь в общем плане."); continue; }
                var name = module.Path.Replace('\\', '/').Split('/')[0];
                if (owners.TryGetValue(name, out var owner) && owner != package.CatalogId)
                    issues.Add($"Папку {name} заменяют разные проекты ESOUI.");
                else owners[name] = package.CatalogId!.Value;
            }
        var providers = packages.Values.SelectMany(p => p.PackageModules.Select(m => (Package: p, Module: m)))
            .GroupBy(x => x.Module.Path.Replace('\\', '/').Split('/')[^1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var local = installed.Where(x => x.Path.Length > 0 && !owners.ContainsKey(Folder(x.Path)))
            .GroupBy(x => Folder(x.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var edges = packages.Keys.ToDictionary(x => x, _ => new HashSet<long>());
        var visitedLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = 0;
        void Require(string requirement, long parent, int depth)
        {
            if (++checks > 2000 || depth > 64 || requirement.Length > 256)
            { issues.Add("Общий список зависимостей слишком велик или глубок."); return; }
            var match = Regex.Match(requirement, @"^([^\s<>=]+)(?:>=([0-9]{1,128}))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !AddonMatcher.IsTopLevelFolder(match.Groups[1].Value))
            { issues.Add("Не поддерживается общее условие: " + requirement); return; }
            var name = match.Groups[1].Value;
            var minimum = match.Groups[2].Success ? match.Groups[2].Value : null;
            if (providers.TryGetValue(name, out var supplied))
            {
                if (supplied.Any(x => !Satisfies(x.Module.BuildVersion, minimum)))
                { issues.Add("Общий план не подтверждает сборку " + requirement + "."); return; }
                foreach (var supply in supplied)
                    if (supply.Package.CatalogId != parent) edges[parent].Add(supply.Package.CatalogId!.Value);
                return;
            }
            if (local.TryGetValue(name, out var present) && present.Count == 1 && present[0].LocalMetadataComplete
                && Satisfies(present[0].BuildVersion, minimum))
            {
                // Для разных корней сохраняем отдельные рёбра через локальные зависимости.
                if (visitedLocal.Add(parent + ":" + name))
                    foreach (var child in present[0].RequiredDependencies) Require(child, parent, depth + 1);
                return;
            }
            issues.Add("После общей замены не подтверждена зависимость " + requirement + ".");
        }
        foreach (var package in packages.Values)
            foreach (var requirement in package.PackageModules.SelectMany(x => x.RequiredDependencies))
                Require(requirement, package.CatalogId!.Value, 0);

        var ordered = new List<Addon>(); var visited = new HashSet<long>(); var visiting = new HashSet<long>();
        void Visit(long id)
        {
            if (visited.Contains(id) || !visiting.Add(id)) return;
            foreach (var dependency in edges[id].Order()) Visit(dependency);
            visiting.Remove(id); visited.Add(id); ordered.Add(packages[id]);
        }
        foreach (var id in packages.Keys.Order()) Visit(id);
        return new(new(ordered[0], ordered, issues.Distinct().ToArray()), skipped.Distinct().ToArray());
    }

    private static string Folder(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));
    private static bool Satisfies(string build, string? minimum) => minimum is null || build.Length is > 0 and <= 128
        && build.All(char.IsAsciiDigit) && BigInteger.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= BigInteger.Parse(minimum, CultureInfo.InvariantCulture);
    private static bool Equivalent(Addon left, Addon right) => left.Version == right.Version
        && left.DependencyMetadataValid == right.DependencyMetadataValid
        && left.PackageModules.Count == right.PackageModules.Count
        && left.PackageModules.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Zip(right.PackageModules.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            .All(x => x.First.Path.Equals(x.Second.Path, StringComparison.OrdinalIgnoreCase)
                && x.First.BuildVersion == x.Second.BuildVersion && x.First.IsLibrary == x.Second.IsLibrary
                && x.First.RequiredDependencies.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(x.Second.RequiredDependencies.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase));
}
