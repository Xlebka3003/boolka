using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;

namespace Boolka;

public sealed class ArchiveDownloader
{
    public const long MaxArchiveBytes = 256L * 1024 * 1024;
    private static readonly HttpClient Network = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly HttpClient client;
    public ArchiveDownloader(HttpClient? client = null) => this.client = client ?? Network;

    public async Task DownloadAsync(long id, string destination, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (id <= 0 || !Path.IsPathFullyQualified(destination)) throw new ArgumentException("Некорректный путь или идентификатор аддона.");
        destination = Path.GetFullPath(destination);
        if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Сохраните архив с расширением .zip.");
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Выбранная папка не найдена.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        var temporary = Path.Combine(directory, $".boolka-{Guid.NewGuid():N}.part");
        try
        {
            using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.mmoui.com/v4/game/ESO/filedetails/{id}.json");
            metadataRequest.Headers.UserAgent.ParseAdd("boolka/0.14");
            using var metadataResponse = await client.SendAsync(metadataRequest, token).ConfigureAwait(false);
            metadataResponse.EnsureSuccessStatusCode();
            using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(metadataStream, cancellationToken: token).ConfigureAwait(false);
            var info = DownloadMetadata.Parse(document.RootElement, id);
            using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
            request.Headers.UserAgent.ParseAdd("boolka/0.14");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaxArchiveBytes) throw new InvalidDataException("Архив превышает лимит 256 МБ.");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long received = 0;
                progress?.Report(new(0, length));
                while (true)
                {
                    var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > MaxArchiveBytes) throw new InvalidDataException("Архив превышает лимит 256 МБ.");
                    await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress?.Report(new(received, length));
                }
                if (length.HasValue && received != length) throw new InvalidDataException("Архив загружен не полностью.");
            }
            await using (var file = File.OpenRead(temporary))
            {
                var signature = new byte[4];
                await file.ReadExactlyAsync(signature, token).ConfigureAwait(false);
                if (!signature.SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) throw new InvalidDataException("Сервер вернул не ZIP-архив.");
                await ValidateDirectoryAsync(file, token).ConfigureAwait(false);
                file.Position = 0;
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
                    if (archive.Entries.Count == 0) throw new InvalidDataException("Архив пуст.");
                if (info.Checksum is not null)
                {
                    file.Position = 0;
                    var actual = Convert.ToHexString(await MD5.HashDataAsync(file, token).ConfigureAwait(false));
                    if (!actual.Equals(info.Checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма архива не совпала. Повторите скачивание.");
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task ValidateDirectoryAsync(FileStream file, CancellationToken token)
    {
        var tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length;
        await file.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException("Повреждён каталог ZIP-архива.");
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4, 2));
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6, 2));
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8, 2));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10, 2));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12, 4));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16, 4));
        var absoluteEnd = file.Length - tail.Length + end;
        if (disk != 0 || centralDisk != 0 || diskEntries != entries || entries is 0 or > 20000 || size > 16 * 1024 * 1024
            || (long)offset + size != absoluteEnd)
            throw new InvalidDataException("Неподдерживаемый ZIP: ZIP64, составной или слишком большой каталог файлов.");
        var directory = new byte[(int)size];
        file.Position = offset;
        await file.ReadExactlyAsync(directory, token).ConfigureAwait(false);
        var position = 0; var count = 0;
        while (position < directory.Length)
        {
            token.ThrowIfCancellationRequested();
            if (directory.Length - position < 46 || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position, 4)) != 0x02014b50 || ++count > 20000)
                throw new InvalidDataException("Повреждён или слишком велик каталог файлов ZIP.");
            position += 46 + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30, 2))
                + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32, 2));
            if (position > directory.Length) throw new InvalidDataException("Повреждён каталог файлов ZIP.");
        }
        if (count != entries) throw new InvalidDataException("Количество файлов ZIP не совпадает с каталогом.");
    }
}




