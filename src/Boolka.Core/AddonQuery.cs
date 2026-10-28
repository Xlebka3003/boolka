namespace Boolka;

public enum AddonSort { Name, Downloads, LastUpdated }
public enum StatusFilter { All, UpdateAvailable, VersionsMatch, Unknown, NotChecked, MissingLibraries, Ignored, Unidentified }
public enum CatalogPresenceFilter { All, Installed, NotInstalled }

public static class AddonQuery
{
    public static IReadOnlyList<Addon> Apply(IReadOnlyList<Addon> source, string query, AddonSort sort, StatusFilter filter, CatalogPresenceFilter presence = CatalogPresenceFilter.All, bool reverse = false)
    {
        query = query.Trim();
        var filtered = source.Where(x => MatchesSearch(x, query));
        if (presence != CatalogPresenceFilter.All)
            filtered = filtered.Where(x => x.IsInstalled == (presence == CatalogPresenceFilter.Installed));
        if (filter != StatusFilter.All)
            filtered = filtered.Where(x => filter switch
            {
                StatusFilter.UpdateAvailable => x.Comparison?.Status == UpdateStatus.UpdateAvailable,
                StatusFilter.VersionsMatch => x.Comparison?.Status == UpdateStatus.VersionsMatch,
                StatusFilter.Unknown => x.Comparison is { Status: UpdateStatus.Unknown, CatalogAddon: not null },
                StatusFilter.Unidentified => x.Comparison is { Status: UpdateStatus.Unknown, CatalogAddon: null },
                StatusFilter.NotChecked => x.Comparison is null || x.Comparison.Status == UpdateStatus.NotChecked,
                StatusFilter.MissingLibraries => x.HasDependencyProblems,
                StatusFilter.Ignored => x.UpdatesIgnored,
                _ => true
            });
        return (sort switch
        {
            // Неизвестные значения остаются внизу при любом направлении.
            AddonSort.Downloads => reverse
                ? filtered.OrderBy(x => !x.DisplayDownloads.HasValue).ThenBy(x => x.DisplayDownloads).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderByDescending(x => x.DisplayDownloads).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            AddonSort.LastUpdated => reverse
                ? filtered.OrderBy(x => !x.DisplayLastUpdated.HasValue).ThenBy(x => x.DisplayLastUpdated).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderByDescending(x => x.DisplayLastUpdated).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => reverse ? filtered.OrderByDescending(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        }).ToList();
    }

    private static bool MatchesSearch(Addon addon, string query)
    {
        bool Contains(string value) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        // Название автора в манифесте может отличаться от каталога. Ищем также
        // по имени самой папки, но не по пути пользователя или родительских папок.
        var folder = System.IO.Path.GetFileName(addon.Path.TrimEnd('\\', '/'));
        return Contains(addon.Name) || Contains(addon.DisplayName) || Contains(addon.Author)
            || Contains(folder) || addon.CatalogFolders.Any(x => Contains(x.Folder));
    }
}
