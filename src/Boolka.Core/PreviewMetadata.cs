using System.Text.Json;

namespace Boolka;

public static class PreviewMetadata
{
    public static Uri? ValidateImageUrl(string? value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && (uri.Host is "cdn-eso.mmoui.com" or "cdn.esoui.com" or "cdn.mmoui.com")
            && uri.AbsolutePath.StartsWith("/preview/", StringComparison.Ordinal) ? uri : null;
    }

    public static Uri? ParseThumbnail(JsonElement root, long expectedId)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && ParseThumbnail(item, expectedId) is { } match) return match;
            return null;
        }
        if (expectedId <= 0 || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var id) || id.ToString() != expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in images.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "thumbUrl", "imageUrl" })
                if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && ValidateImageUrl(value.GetString()) is { } uri) return uri;
        }
        return null;
    }
}
