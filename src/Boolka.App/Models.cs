namespace Boolka;

public sealed record Addon(string Name, string Version, string Author, string Description, string Path, string Dependencies, string? WebUrl = null)
{
    // Автоматизация WPF запрашивает текст строки. Автоматический ToString у record
    // зацикливается на DisplayCatalog, который может ссылаться на этот же объект.
    public override string ToString() => Name;
    public long? CatalogId { get; init; }
    public long CategoryId { get; init; }
    public IReadOnlyList<CatalogFolder> CatalogFolders { get; init; } = Array.Empty<CatalogFolder>();
    public bool IsModuleContainer { get; init; }
    public string BuildVersion { get; init; } = "";
    public string? ManifestVersion { get; init; }
    public string VersionLabel => Boolka.VersionLabel.Format(this);
    public long? InstalledReleaseId { get; init; }
    public string? InstalledReleaseVersion { get; init; }
    public AddonComparison? Comparison { get; init; }
    public long? Downloads { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public Addon? DisplayCatalog => Comparison?.CatalogAddon ?? (CatalogId.HasValue ? this : null);
    public string DisplayName => Comparison?.CatalogAddon?.Name ?? Name;
    public long? DisplayDownloads => DisplayCatalog?.Downloads;
    public long DisplayCategoryId => DisplayCatalog?.CategoryId ?? CategoryId;
    public DateTimeOffset? DisplayLastUpdated => DisplayCatalog?.LastUpdated;
    public long? PreviewId => DisplayCatalog?.CatalogId;
    public bool IsInstalled { get; init; }
    public bool UnconfirmedInstallation { get; init; }
    public bool HasUpdate { get; init; }
    public bool UpdatesIgnored { get; init; }
    public DependencyHealthReport? DependencyHealth { get; init; }
    public IReadOnlyList<CatalogModule> LocalModules { get; init; } = Array.Empty<CatalogModule>();
    public bool LocalMetadataComplete { get; init; } = true;
    public bool HasDependencyProblems => DependencyHealth?.HasProblems == true;
    public string DependencyStatusText => DependencyHealth is { } health && (health.HasProblems || health.Unknown.Count > 0) ? health.StatusText : "";
    public string DependencyStatusColor => ThemePalette.Map(HasDependencyProblems ? "#F28B93" : "#9AA3B5");
    public bool CanInstall => !UnconfirmedInstallation && PreviewId is > 0 && (!IsInstalled || HasUpdate && !UpdatesIgnored);
    public string InstallActionBackground => HasUpdate && CanInstall ? ThemePalette.Map("#7DDDC0") : "Transparent";
    public string InstallActionForeground => HasUpdate && CanInstall && ThemePalette.IsLight ? "#FAF8F4"
        : ThemePalette.Map(HasUpdate && CanInstall ? "#14121D" : "#E9EDF5");
    public string InstallationLabel => UnconfirmedInstallation ? UiText.Get("Папка уже есть") : !IsInstalled ? UiText.Get("Установить") : UpdatesIgnored ? UiText.Get("Пропущен") : HasUpdate ? UiText.Get("Обновить") : UiText.Get("Установлен");
    public IReadOnlyList<CatalogModule> PackageModules { get; init; } = Array.Empty<CatalogModule>();
    public IReadOnlyList<string> RequiredDependencies { get; init; } = Array.Empty<string>();
    public bool DependencyMetadataValid { get; init; } = true;
    public bool CanDownload => PreviewId is > 0;
    public string DownloadLabel => UiText.Get("Скачать ZIP");
    public string DownloadsText => DisplayDownloads?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo(UiText.Language == "en" ? "en-US" : "ru-RU")) ?? "—";
    public string UpdatedText => DisplayLastUpdated?.ToLocalTime().ToString("dd.MM.yyyy") ?? "—";
    public string MetadataText => UiText.Format($"↓ {DownloadsText}  ·  Обновлён {UpdatedText}");
}
public sealed record CatalogFolder(string Folder, string BuildVersion);
public sealed record CatalogModule(string Path, string BuildVersion, IReadOnlyList<string> RequiredDependencies, bool IsLibrary = false);
public enum UpdateStatus { NotChecked, UpdateAvailable, VersionsMatch, Unknown }
public sealed record AddonComparison(Addon Addon, Addon? CatalogAddon, UpdateStatus Status, string Reason, string? CatalogBuildVersion = null)
{
    public string StatusText => Status switch
    {
        UpdateStatus.UpdateAvailable => UiText.Get("Обновление доступно"),
        UpdateStatus.VersionsMatch => UiText.Get("Версии совпадают"),
        UpdateStatus.Unknown => UiText.Get(CatalogAddon is null ? "Проект не определён" : "Версия не определена"),
        _ => UiText.Get("Не проверено")
    };
    public string StatusColor => Status switch
    {
        UpdateStatus.UpdateAvailable => "#E8C17A",
        UpdateStatus.VersionsMatch => "#94D6AE",
        _ => "#9AA3B5"
    };
}
public sealed record ScanResult(IReadOnlyList<Addon> Addons, int Skipped);

public interface IGameProvider
{
    string Id { get; }
    string Name { get; }
    Task<ScanResult> ScanAsync(string folder);
    Task<IReadOnlyList<Addon>> GetCatalogAsync(CancellationToken cancellationToken);
}

public sealed record AddonCategory(long Id, string Name)
{
    public string OriginalName { get; init; } = Name;
    public override string ToString() => Id == 0 ? UiText.Get("Все категории") : Id == -1 ? UiText.Get("Без категории") : Name;
}

public interface ICategoryProvider
{
    Task<IReadOnlyList<AddonCategory>> GetCategoriesAsync(CancellationToken cancellationToken);
}
