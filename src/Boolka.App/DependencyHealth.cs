using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed record DependencyHealthReport(IReadOnlyList<string> Missing, IReadOnlyList<string> Outdated, IReadOnlyList<string> Unknown)
{
    public bool HasProblems => Missing.Count > 0 || Outdated.Count > 0;
    public string StatusText => Missing.Count > 0 && Outdated.Count > 0 ? UiText.Format($"Не хватает или устарели библиотеки: {Missing.Count + Outdated.Count}")
        : Missing.Count > 0 ? UiText.Format($"Не хватает библиотек: {Missing.Count}") : Outdated.Count > 0 ? UiText.Format($"Нужно обновить библиотеки: {Outdated.Count}")
        : Unknown.Count > 0 ? UiText.Get("Не удалось проверить библиотеки") : UiText.Get("Зависимости в порядке");
    public string Details => !HasProblems && Unknown.Count == 0 ? UiText.Get("Проблем с обязательными библиотеками не найдено.")
        : string.Join("\n", Missing.Select(x => UiText.Get("Отсутствует: ") + x).Concat(Outdated.Select(x => UiText.Get("Устарела: ") + x)).Concat(Unknown.Select(x => UiText.Get("Не определено: ") + x)));
}

/// <summary>Evaluates actual local manifests. Catalog module metadata is never installation evidence.</summary>
public static class DependencyHealth
{
    public static DependencyHealthReport Evaluate(Addon root, IReadOnlyList<Addon> installed)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top = installed.Where(x => x.Path.Length > 0).GroupBy(x => Path.GetFileName(x.Path.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        var nested = installed.SelectMany(x => x.LocalModules).Where(x => x.Path.Replace('\\', '/').Contains('/'))
            .Select(x => x.Path.Replace('\\', '/').Split('/')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var complete = installed.All(x => x.LocalMetadataComplete);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var edges = 0;
        if (!root.LocalMetadataComplete) unknown.Add(UiText.Get("Не все вложенные манифесты удалось прочитать."));
        void Require(string requirement, int depth)
        {
            if (++edges > 2000 || depth > 64) { unknown.Add(UiText.Get("Список зависимостей слишком велик или глубок.")); return; }
            if (requirement.Length > 256) { unknown.Add(UiText.Get("Слишком длинная запись зависимости.")); return; }
            var match = Regex.Match(requirement, @"^([^\s<>=]+)(?:>=([0-9]{1,128}))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !AddonMatcher.IsTopLevelFolder(match.Groups[1].Value) || match.Groups[1].Value.Contains('~')
                || match.Groups[1].Value.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
            { unknown.Add(requirement + UiText.Get(" — неподдерживаемое условие.")); return; }
            var name = match.Groups[1].Value; var minimum = match.Groups[2].Value;
            if (!top.TryGetValue(name, out var candidates))
            {
                if (nested.Contains(name)) unknown.Add(requirement + UiText.Get(" — найден вложенный манифест; его загрузка игрой не подтверждена."));
                else if (!complete) unknown.Add(requirement + UiText.Get(" — часть локальных манифестов недоступна."));
                else missing.Add(requirement);
                return;
            }
            if (candidates.Count != 1) { unknown.Add(requirement + UiText.Get(" — несколько локальных модулей с таким именем.")); return; }
            var addon = candidates[0];
            if (minimum.Length > 0)
            {
                if (!TryBuild(addon.BuildVersion, out var build)) unknown.Add(requirement + UiText.Get(" — номер установленной сборки не определён."));
                else if (build < BigInteger.Parse(minimum))
                {
                    if (nested.Contains(name)) unknown.Add(requirement + UiText.Get(" — основная сборка устарела, но найдена вложенная копия с неподтверждённой загрузкой."));
                    else outdated.Add(requirement + UiText.Get(" (установлена сборка ") + addon.BuildVersion + ")");
                }
            }
            if (!addon.LocalMetadataComplete) unknown.Add(name + UiText.Get(" — не все вложенные манифесты доступны."));
            if (visited.Add(name)) foreach (var child in addon.RequiredDependencies) Require(child, depth + 1);
        }
        foreach (var requirement in root.RequiredDependencies) Require(requirement, 0);
        return new(missing.Order(StringComparer.OrdinalIgnoreCase).ToArray(), outdated.Order(StringComparer.OrdinalIgnoreCase).ToArray(), unknown.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private static bool TryBuild(string text, out BigInteger value)
    { value = default; return text.Length is > 0 and <= 128 && text.All(char.IsAsciiDigit) && BigInteger.TryParse(text, out value); }
}
