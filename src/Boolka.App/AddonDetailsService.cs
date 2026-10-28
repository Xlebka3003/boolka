using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Boolka;

public sealed record AddonDetails(string Description, IReadOnlyList<Uri> Images);

/// <summary>Read-only remote information. Description markup is converted to inert plain text.</summary>
public sealed class AddonDetailsService
{
    public const int MaxMetadataBytes = 4 * 1024 * 1024;
    public const int MaxImageBytes = 12 * 1024 * 1024;
    public const int MaxDescriptionCharacters = 256 * 1024;
    public const int MaxImages = 24;
    private sealed record Cached<T>(T Value, DateTimeOffset Expires);
    private static readonly HttpClient Network = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(3);
    private readonly ConcurrentDictionary<long, Cached<AddonDetails>> descriptions = new();
    private readonly ConcurrentDictionary<string, Cached<BitmapSource?>> images = new();
    public AddonDetailsService(HttpClient? client = null) => this.client = client ?? Network;

    public async Task<AddonDetails> GetAsync(long id, CancellationToken token)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        token.ThrowIfCancellationRequested();
        if (descriptions.TryGetValue(id, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (descriptions.TryGetValue(id, out cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
            var bytes = await FetchAsync(new Uri($"https://api.mmoui.com/v4/game/ESO/filedetails/{id}.json"), MaxMetadataBytes, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var details = Parse(document.RootElement, id);
            token.ThrowIfCancellationRequested();
            Trim(descriptions, 128); descriptions[id] = new(details, DateTimeOffset.UtcNow.AddMinutes(15));
            return details;
        }
        finally { gate.Release(); }
    }

    public async Task<BitmapSource?> LoadImageAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (uri is null || !uri.IsAbsoluteUri) return null;
        var validated = PreviewMetadata.ValidateImageUrl(uri.AbsoluteUri);
        if (validated is null) return null;
        var key = validated.AbsoluteUri;
        if (images.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await gate.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        try
        {
            if (images.TryGetValue(key, out cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
            var bytes = await FetchAsync(validated, MaxImageBytes, timeout.Token).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count is 0 or > 100) throw new InvalidDataException("Неподдерживаемое изображение.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
                throw new InvalidDataException("Изображение слишком большое.");
            var scale = Math.Min(1, Math.Min(1280.0 / frame.PixelWidth, 960.0 / frame.PixelHeight));
            stream.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
            bitmap.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            timeout.Token.ThrowIfCancellationRequested();
            Trim(images, 16); images[key] = new(bitmap, DateTimeOffset.UtcNow.AddMinutes(15));
            return bitmap;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or NotSupportedException
            or ArgumentException or InvalidOperationException or OperationCanceledException or System.Runtime.InteropServices.COMException or FileFormatException or OverflowException)
        {
            Trim(images, 16); images[key] = new(null, DateTimeOffset.UtcNow.AddMinutes(1));
            return null;
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> FetchAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("boolka/0.14");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Ответ сервера слишком велик.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) break;
            if (output.Length + count > maximum) throw new InvalidDataException("Ответ сервера слишком велик.");
            output.Write(buffer, 0, count);
        }
        if (response.Content.Headers.ContentLength is { } length && length != output.Length) throw new InvalidDataException("Неполный ответ сервера.");
        return output.ToArray();
    }

    public static AddonDetails Parse(JsonElement root, long expectedId)
    {
        if (expectedId <= 0) throw new InvalidDataException("Не указан проект ESOUI.");
        if (root.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (++count > 1000) break;
                if (HasId(item, expectedId)) return Parse(item, expectedId);
            }
            throw new InvalidDataException("В ответе ESOUI нет выбранного аддона.");
        }
        if (!HasId(root, expectedId)) throw new InvalidDataException("Идентификатор описания ESOUI не совпал.");
        var description = root.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String ? ToPlainText(text.GetString()!) : "";
        var found = new List<Uri>(); var unique = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("images", out var gallery) && gallery.ValueKind == JsonValueKind.Array)
        {
            var examined = 0;
            foreach (var item in gallery.EnumerateArray())
            {
                if (++examined > 256 || found.Count >= MaxImages) break;
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("imageUrl", out var image) && image.ValueKind == JsonValueKind.String
                    && PreviewMetadata.ValidateImageUrl(image.GetString()) is { } uri && unique.Add(uri.AbsoluteUri)) found.Add(uri);
            }
        }
        return new(description.Length > 0 ? description : "Автор не добавил описание.", found.AsReadOnly());
    }

    public static string ToPlainText(string value)
    {
        var truncated = value.Length > MaxDescriptionCharacters;
        value = WebUtility.HtmlDecode(value[..Math.Min(value.Length, MaxDescriptionCharacters)]).Replace("\r\n", "\n").Replace('\r', '\n');
        string Replace(string pattern, string replacement)
        {
            try { return Regex.Replace(value, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250)); }
            catch (RegexMatchTimeoutException ex) { throw new InvalidDataException("Описание слишком сложно для отображения.", ex); }
        }
        value = Replace(@"<script\b[^>]*>[\s\S]*?</script\s*>", "");
        value = Replace(@"<style\b[^>]*>[\s\S]*?</style\s*>", "");
        value = Replace(@"\[(?:img|youtube|video)(?:=[^\]]*)?\][\s\S]*?\[/(?:img|youtube|video)\]", "");
        value = Replace(@"<li\b[^>]*>|\[\*\]", "\n• ");
        value = Replace(@"<br\s*/?>|</(?:p|div|h[1-6]|li|tr)\s*>|\[/?(?:list|quote|center|left|right|indent|code)(?:=[^\]]*)?\]", "\n");
        value = Replace(@"<[^>]*>", "");
        value = Replace(@"\[/?(?:b|i|u|s|size|color|font|url|email|align|spoiler|table|tr|td|th|anchor|hr)(?:=[^\]]*)?\]", "");
        value = Replace(@"\|c[0-9a-f]{6}|\|r", "");
        value = string.Join("\n", value.Split('\n').Select(line => line.Trim()));
        value = Replace(@"\n{3,}", "\n\n").Trim();
        return value + (truncated ? "\n\n[Описание сокращено из-за размера.]" : "");
    }
    private static bool HasId(JsonElement item, long id) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number && long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var actual) && actual == id;
    private static void Trim<TKey, TValue>(ConcurrentDictionary<TKey, Cached<TValue>> cache, int maximum) where TKey : notnull
    { if (cache.Count >= maximum) foreach (var key in cache.OrderBy(x => x.Value.Expires).Take(Math.Max(1, maximum / 4)).Select(x => x.Key)) cache.TryRemove(key, out _); }
}

