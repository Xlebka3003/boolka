using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed class EsoProvider : IGameProvider, ICategoryProvider
{
    public string Id => "ESO";
    public string Name => "The Elder Scrolls Online";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 16 * 1024 * 1024
    };
    private static readonly Regex ColorMarkup = new(@"\|c[0-9a-f]{6}|\|r", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ESO stores inline color markup in display metadata. Keep files intact and
    // normalize before sorting/searching so every view uses the readable text.
    private static string PlainText(string text) => ColorMarkup.Replace(text, "").Trim();

    public Task<ScanResult> ScanAsync(string folder) => Task.Run(() =>
    {
        var addons = new List<Addon>();
        var skipped = 0;
        // Recognize direct addon manifests; bounded nested discovery never follows links or SavedVariables.
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                var name = System.IO.Path.GetFileName(directory);
                if (name.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) continue;
                var manifest = new[] { System.IO.Path.Combine(directory, name + ".txt"), System.IO.Path.Combine(directory, name + ".addon") }
                    .FirstOrDefault(File.Exists);
                if (manifest is null)
                {
                    var scan = LocalModuleScanner.Scan(directory, new(name, "", []));
                    var modules = scan.Modules.Skip(1).ToArray();
                    if (modules.Length > 0)
                    {
                        var builds = modules.Select(module => module.BuildVersion).Distinct(StringComparer.Ordinal).ToArray();
                        var dependencies = modules.SelectMany(module => module.RequiredDependencies).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        addons.Add(new(name, "Не указана", "Автор не указан", UiText.Get("Пакет с вложенными модулями."), directory, string.Join(" ", dependencies))
                        {
                            IsModuleContainer = true,
                            BuildVersion = builds.Length == 1 ? builds[0] : "",
                            RequiredDependencies = dependencies,
                            LocalModules = modules,
                            LocalMetadataComplete = scan.Complete
                        });
                    }
                    continue;
                }
                if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifest).Length > 1024 * 1024) { skipped++; continue; }
                var fields = File.ReadLines(manifest).Where(x => x.StartsWith("##") && x.Contains(':'))
                    .Select(x => x[2..].Split(':', 2)).GroupBy(x => ManifestFields.Normalize(x[0]), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Key.Equals("DependsOn", StringComparison.OrdinalIgnoreCase) ? string.Join(" ", x.Select(y => y[1].Trim())) : x.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
                string Get(string key, string fallback = "")
                {
                    var value = PlainText(fields.GetValueOrDefault(key, ""));
                    return string.IsNullOrWhiteSpace(value) ? fallback : value;
                }
                var build = Get("AddOnVersion");
                var version = Get("Version", build.Length > 0 ? $"Сборка {build}" : "Не указана");
                var required = Get("DependsOn").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var localModules = LocalModuleScanner.Scan(directory, new(name, build, required));
                addons.Add(new(Get("Title", name), version, Get("Author", "Автор не указан"),
                    Get("Description", "Описание отсутствует в манифесте аддона."), directory, Get("DependsOn", "Не указаны"))
                { BuildVersion = build, ManifestVersion = Get("Version"), RequiredDependencies = required, LocalModules = localModules.Modules, LocalMetadataComplete = localModules.Complete });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        var releases = InstalledReleaseStore.LoadVerified(folder);
        return new ScanResult(addons.Select(x => releases.TryGetValue(Path.GetFileName(x.Path), out var release)
                ? x with { InstalledReleaseId = release.Id, InstalledReleaseVersion = release.Version } : x)
            .Select(x => skipped > 0 ? x with { LocalMetadataComplete = false } : x)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), skipped);
    });

    public async Task<IReadOnlyList<Addon>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mmoui.com/v4/game/ESO/filelist.json");
        request.Headers.UserAgent.ParseAdd("boolka/0.14");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseCatalog(document.RootElement);
    }

    public async Task<IReadOnlyList<AddonCategory>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mmoui.com/v4/game/ESO/categorylist.json");
        request.Headers.UserAgent.ParseAdd("boolka/0.19");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseCategories(document.RootElement);
    }

    public static IReadOnlyList<AddonCategory> ParseCategories(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Неверный формат категорий ESOUI.");
        var result = new Dictionary<long, AddonCategory>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
                || !long.TryParse(id.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
                || !item.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String) continue;
            var name = PlainText(WebUtility.HtmlDecode(title.GetString()!)).Trim();
            if (name.Length is > 0 and <= 200) result.TryAdd(number, new(number, name));
        }
        if (result.Count == 0) throw new InvalidDataException("Список категорий ESOUI пуст.");
        return result.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<Addon> ParseCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Формат ответа каталога отличается от ожидаемого.");
        var addons = new List<Addon>();
        var ids = new HashSet<long>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string Get(string key, string fallback = "")
            {
                if (!item.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return fallback;
                var text = PlainText(WebUtility.HtmlDecode(value.ToString()));
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            var name = Get("title");
            if (name.Length == 0 || !long.TryParse(Get("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id)) continue;
            var folders = new List<CatalogFolder>();
            var modules = new List<CatalogModule>();
            var dependencyMetadataValid = true;
            if (item.TryGetProperty("addons", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) { dependencyMetadataValid = false; continue; }
                    var folder = path.GetString()!;
                    if (!DependencyResolver.IsSafePackagePath(folder)) { dependencyMetadataValid = false; continue; }
                    var build = entry.TryGetProperty("addOnVersion", out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString().Trim() : "";
                    var required = entry.TryGetProperty("requiredDependencies", out var requirements) && requirements.ValueKind == JsonValueKind.Array
                        ? requirements.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
                    if (entry.TryGetProperty("requiredDependencies", out var rawRequired) && (rawRequired.ValueKind != JsonValueKind.Array || rawRequired.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))) dependencyMetadataValid = false;
                    modules.Add(new(folder.Replace('\\', '/'), build, required, entry.TryGetProperty("library", out var library) && library.ValueKind == JsonValueKind.True));
                    if (AddonMatcher.IsTopLevelFolder(folder)) folders.Add(new(folder, build));
                }
            // Корни вложенных модулей нужны для сопоставления папки-контейнера, но не являются модулями игры.
            foreach (var group in modules.GroupBy(module => module.Path.Split('/')[0], StringComparer.OrdinalIgnoreCase))
                if (!folders.Any(folder => folder.Folder.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var builds = group.Select(module => module.BuildVersion).Distinct(StringComparer.Ordinal).ToArray();
                    folders.Add(new(group.Key, builds.Length == 1 ? builds[0] : ""));
                }
            long? ReadNonNegative(string key) => long.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
            DateTimeOffset? updated = null;
            var milliseconds = ReadNonNegative("lastUpdate");
            if (milliseconds is > 0 and <= 253402300799999)
                updated = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
            addons.Add(new(name, Get("version", "Не указана"), Get("author", "Автор не указан"),
                "Аддон из каталога ESOUI. Полное описание, изображения и история изменений доступны на странице автора.",
                "", string.Join("\n", modules.SelectMany(x => x.RequiredDependencies).Distinct()), $"https://www.esoui.com/downloads/info{id}")
            { CatalogId = id, CatalogChecksum = Get("checksum"), CategoryId = ReadNonNegative("categoryId") ?? 0, CatalogFolders = folders, PackageModules = modules, DependencyMetadataValid = dependencyMetadataValid, Downloads = ReadNonNegative("downloads"), LastUpdated = updated });
        }
        if (addons.Count == 0) throw new InvalidDataException("Не удалось распознать записи каталога. Нужна проверка формата API.");
        return addons.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}




