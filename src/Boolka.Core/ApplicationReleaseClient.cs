using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boolka;

internal sealed record ApplicationRelease(Version Version, Uri Download, long Size, string Sha256);

internal sealed class ApplicationReleaseClient : IDisposable
{
    internal const string Repository = "Xlebka3003/boolka";
    internal const long MaximumDownload = 200L * 1024 * 1024;
    private readonly HttpClient client;

    internal ApplicationReleaseClient(HttpClient? client = null)
    {
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.client.Timeout = Timeout.InfiniteTimeSpan;
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("boolka-update/1.0");
    }

    internal static Version ReadVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^\d{1,6}\.\d{1,6}\.\d{1,6}$", RegexOptions.CultureInvariant)
            || !Version.TryParse(text, out var version)) throw new InvalidDataException("Неверная версия приложения.");
        return version;
    }

    internal async Task<ApplicationRelease?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await GetAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), timeout.Token);
        // Закрытый репозиторий и отсутствие выпусков не требуют уведомления пользователя.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        await CopyBoundedAsync(stream, data, 1024 * 1024, null, timeout.Token);
        return Parse(data.ToArray(), current);
    }

    internal static ApplicationRelease? Parse(byte[] json, Version current)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("Слишком большой ответ обновления.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException("Неверный тег выпуска.");
        var version = ReadVersion(tag[1..]);
        if (version <= current) return null;
        var name = $"boolka-{version}-windows-x64-standalone.zip";
        var assets = root.GetProperty("assets");
        if (assets.GetArrayLength() > 100) throw new InvalidDataException("Слишком много файлов выпуска.");
        var matches = assets.EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Не найден единственный архив Windows x64.");
        var asset = matches[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var address = asset.GetProperty("browser_download_url").GetString();
        var expected = $"https://github.com/{Repository}/releases/download/v{version}/{name}";
        if (size is <= 0 or > MaximumDownload || address != expected
            || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Неверные сведения об архиве обновления.");
        return new(version, new Uri(expected), size, digest[7..]);
    }

    internal async Task DownloadAsync(ApplicationRelease release, string destination, IProgress<long>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await GetAsync(release.Download, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            throw new InvalidDataException("Размер архива обновления изменился.");
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await CopyBoundedAsync(input, output, release.Size, progress, timeout.Token);
        if (new FileInfo(destination).Length != release.Size) throw new InvalidDataException("Архив обновления скачан не полностью.");
        await using var file = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token));
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (!AllowedAddress(address)) throw new InvalidDataException("Недопустимый адрес обновления.");
            var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            address = location is null ? throw new InvalidDataException("Не указан адрес перенаправления.") : new Uri(address, location);
        }
        throw new InvalidDataException("Слишком много перенаправлений обновления.");
    }

    internal static bool AllowedAddress(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, IProgress<long>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("Превышен размер обновления.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Report(total);
        }
    }

    public void Dispose() => client.Dispose();
}
