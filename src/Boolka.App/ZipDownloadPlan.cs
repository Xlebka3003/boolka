namespace Boolka;

/// <summary>Сохраняет выбранный релиз в ZIP, даже если проект уже установлен.</summary>
public static class ZipDownloadPlan
{
    public static DependencyPlan? Build(Addon addon, IReadOnlyList<Addon> catalog, IReadOnlyList<Addon> installed)
    {
        if (addon.PreviewId is not > 0) return null;
        var candidates = catalog.Where(x => x.CatalogId == addon.PreviewId).Take(2).ToList();
        if (candidates.Count != 1) return null;
        // Установленные библиотеки учитываются, но основной архив всегда остаётся в плане.
        return DependencyResolver.Build(candidates[0], catalog, installed, includeRoot: true);
    }
}
