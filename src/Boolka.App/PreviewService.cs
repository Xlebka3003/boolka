using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Boolka;

public static class PreviewService
{
    private sealed record Entry(BitmapSource? Image, DateTimeOffset Expires);
    private static readonly ConcurrentDictionary<long, Entry> Cache = new();
    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    public static async Task<BitmapSource?> GetAsync(long id, CancellationToken token)
    {
        if (id <= 0) return null;
        if (Cache.TryGetValue(id, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Image;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (Cache.TryGetValue(id, out cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Image;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mmoui.com/v4/game/ESO/filedetails/{id}.json");
            request.Headers.UserAgent.ParseAdd("boolka/0.14");
            using var response = await Client.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
            var uri = PreviewMetadata.ParseThumbnail(document.RootElement, id);
            BitmapSource? image = null;
            if (uri is not null)
            {
                using var imageRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                imageRequest.Headers.UserAgent.ParseAdd("boolka/0.14");
                using var imageResponse = await Client.SendAsync(imageRequest, token).ConfigureAwait(false);
                imageResponse.EnsureSuccessStatusCode();
                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 8192 || frame.PixelHeight > 8192 || (long)frame.PixelWidth * frame.PixelHeight > 20_000_000)
                    throw new InvalidDataException("Превью слишком большое.");
                stream.Position = 0;
                var bitmap = new BitmapImage();
                var scale = Math.Min(1, Math.Min(160.0 / frame.PixelWidth, 160.0 / frame.PixelHeight));
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
                bitmap.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
                bitmap.StreamSource = stream; bitmap.EndInit();
                bitmap.Freeze(); image = bitmap;
            }
            token.ThrowIfCancellationRequested();
            Remember(id, new(image, DateTimeOffset.UtcNow.AddHours(1)));
            return image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or OperationCanceledException or System.Runtime.InteropServices.COMException or System.IO.FileFormatException or OverflowException)
        {
            Remember(id, new(null, DateTimeOffset.UtcNow.AddMinutes(2)));
            return null;
        }
        finally { Gate.Release(); }
    }

    private static void Remember(long id, Entry value)
    {
        if (Cache.Count >= 256)
            foreach (var key in Cache.Keys.Take(32)) Cache.TryRemove(key, out _);
        Cache[id] = value;
    }
}




