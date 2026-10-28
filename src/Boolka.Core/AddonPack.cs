using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Boolka;

public sealed record PackAddon(long Id, string Name);
public sealed record AddonPack(int FormatVersion, string Game, string Name, string Author, string Description, IReadOnlyList<PackAddon> Addons);
public sealed record PackImportApproval(AddonPack Pack, string Fingerprint, string TargetFolder)
{
    public string? CatalogFingerprint { get; init; }
    public IReadOnlyDictionary<long, string>? Archives { get; init; }
}
public sealed record PackPlan(DependencyPlan? Plan, IReadOnlyList<string> Skipped, IReadOnlyList<string> Review)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        Plan?.Packages.Select(x => new { x.CatalogId, x.Name, x.Version, x.CatalogChecksum, x.DependencyMetadataValid, x.CatalogFolders, x.PackageModules })))));
}

public static class PackFile
{
    public const int MaxBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AddonPack Read(string path)
    {
        CheckExtension(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException(UiText.Get("Файл набора слишком большой."));
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return Parse(bytes);
    }

    public static AddonPack Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException(UiText.Get("Файл набора слишком большой."));
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        void CheckKeys(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException(UiText.Get("Повторяющиеся поля в файле набора."));
                    CheckKeys(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckKeys(item);
        }
        CheckKeys(document.RootElement);
        var pack = JsonSerializer.Deserialize<AddonPack>(bytes, Options) ?? throw new InvalidDataException(UiText.Get("Пустой файл набора."));
        Validate(pack);
        return pack;
    }

    public static void Validate(AddonPack pack)
    {
        bool Text(string? value, int limit, bool required = false, bool multiline = false) => value is not null && value.Length <= limit
            && (!required || !string.IsNullOrWhiteSpace(value)) && !value.Any(c => char.IsControl(c) && (!multiline || c is not ('\n' or '\r' or '\t')));
        if (pack.FormatVersion != 1 || pack.Game != "ESO" || !Text(pack.Name, 120, true)
            || !Text(pack.Author, 120) || !Text(pack.Description, 2000, multiline: true) || pack.Addons is null
            || pack.Addons.Count is < 1 or > 100 || pack.Addons.Any(x => x is null || x.Id <= 0 || !Text(x.Name, 200, true))
            || pack.Addons.Select(x => x.Id).Distinct().Count() != pack.Addons.Count)
            throw new InvalidDataException(UiText.Get("Неверный формат набора. Поддерживается ESO, версия 1, от 1 до 100 уникальных модов."));
    }

    public static void Write(string path, AddonPack pack)
    {
        CheckExtension(path);
        Validate(pack);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pack, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException(UiText.Get("Файл набора слишком большой."));
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "pack-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CheckExtension(string path)
    {
        if (!Path.GetExtension(path).Equals(".boolka", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.Get("Выберите файл с расширением .boolka."));
    }
}

public static class PackPlanner
{
    public static PackPlan Build(AddonPack pack, IReadOnlyList<Addon> catalog, IReadOnlyList<Addon> installed, IReadOnlySet<long> ignored)
    {
        PackFile.Validate(pack);
        var comparisons = AddonMatcher.Compare(installed, catalog, true);
        var presentIds = comparisons.Where(x => x.CatalogAddon?.CatalogId is > 0).Select(x => x.CatalogAddon!.CatalogId!.Value).ToHashSet();
        var folders = installed.Select(x => Path.GetFileName(Path.TrimEndingDirectorySeparator(x.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<long, Addon>();
        var owners = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var review = new List<string>();
        foreach (var requested in pack.Addons)
        {
            var choices = catalog.Where(x => x.CatalogId == requested.Id).ToArray();
            var label = choices.Length == 1 ? choices[0].Name : requested.Name + " (#" + requested.Id + ")";
            void Skip(string reason) { var text = label + " — " + reason; skipped.Add(text); review.Add(text); }
            if (presentIds.Contains(requested.Id)) { Skip(UiText.Get("уже установлен; оставляем без изменений")); continue; }
            if (choices.Length != 1) { Skip(UiText.Get("проект недоступен в ESOUI")); continue; }
            if (ignored.Contains(requested.Id)) { Skip(UiText.Get("проект исключён пользователем")); continue; }
            var plan = ArchiveDependencyPlanner.Build(choices[0], catalog, installed);
            if (!plan.IsReady) { Skip(string.Join("; ", plan.Issues.Select(UiError.Detail))); continue; }
            // Импорт только добавляет. Даже требуемая новая библиотека не заменяет существующую папку.
            if (plan.Packages.Any(x => x.CatalogId is not > 0 || ignored.Contains(x.CatalogId.Value)
                || x.PackageModules.Any(m => folders.Contains(m.Path.Replace('\\', '/').Split('/')[0]))))
            { Skip(UiText.Get("требуется замена существующей папки или исключённой библиотеки; обновите её отдельно")); continue; }
            var newPackages = plan.Packages.Where(x => !packages.ContainsKey(x.CatalogId!.Value)).ToArray();
            var candidateOwners = new Dictionary<string, long>(owners, StringComparer.OrdinalIgnoreCase);
            var conflict = false;
            foreach (var candidate in newPackages)
                foreach (var module in candidate.PackageModules)
                {
                    var root = module.Path.Replace('\\', '/').Split('/')[0];
                    if (candidateOwners.TryGetValue(root, out var owner) && owner != candidate.CatalogId) conflict = true;
                    candidateOwners[root] = candidate.CatalogId!.Value;
                }
            if (packages.Count + newPackages.Length > 100 || conflict)
            { Skip(UiText.Get("конфликт папок или превышен лимит 100 пакетов")); continue; }
            foreach (var package in newPackages)
            {
                packages.Add(package.CatalogId!.Value, package);
                foreach (var module in package.PackageModules) owners[module.Path.Replace('\\', '/').Split('/')[0]] = package.CatalogId.Value;
            }
            review.Add(label + " — " + UiText.Get("будет установлен"));
        }
        var ordered = packages.Values.ToArray();
        return new(ordered.Length == 0 ? null : new(ordered[0], ordered, Array.Empty<string>()), skipped, review);
    }
}
