using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed record DownloadInfo(Uri Url, string FileName, string? Checksum);
public sealed record DownloadProgress(long Bytes, long? Total);

public static class DownloadMetadata
{
    public static string SuggestedFileName(string name, string version, long id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var label = new string($"{name}-{version}".Where(c => !invalid.Contains(c) && !char.IsControl(c)).Take(100).ToArray()).Trim(' ', '.');
        return $"{id}-{(label.Length > 0 ? label : "addon")}.zip";
    }

    public static DownloadInfo Parse(JsonElement root, long expectedId)
    {
        if (expectedId <= 0) throw new InvalidDataException("Не указан проект ESOUI.");
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (HasId(item, expectedId)) return Parse(item, expectedId);
            throw new InvalidDataException("ESOUI не вернул сведения о выбранном аддоне.");
        }
        if (!HasId(root, expectedId)) throw new InvalidDataException("Идентификатор ответа ESOUI не совпадает с выбранным аддоном.");
        string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var raw = Text("downloadUri");
        if (raw.Length > 4096 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.esoui.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/downloads/getfile.php")
            throw new InvalidDataException("ESOUI вернул неподдерживаемую ссылку скачивания.");
        var ids = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
            .Where(x => Uri.UnescapeDataString(x[0]) == "id").ToList();
        if (ids.Count != 1 || ids[0].Length != 2 || !long.TryParse(Uri.UnescapeDataString(ids[0][1]), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != expectedId)
            throw new InvalidDataException("Идентификатор ссылки скачивания не совпадает с выбранным аддоном.");
        var checksum = Text("checksum").Trim();
        if (checksum.Length > 0 && !Regex.IsMatch(checksum, "^[0-9a-fA-F]{32}$"))
            throw new InvalidDataException("Некорректная контрольная сумма архива.");
        return new(uri, SuggestedFileName(Text("title"), Text("version"), expectedId), checksum.Length == 0 ? null : checksum);
    }

    private static bool HasId(JsonElement item, long expectedId) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("id", out var id) && (id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        && long.TryParse(id.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number == expectedId;
}
