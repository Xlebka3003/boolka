using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Boolka;

internal sealed record PreparedPack(PackPlan Plan, IReadOnlyList<Addon> Catalog);

/// <summary>Уточняет состав импорта до согласования, не изменяя папку AddOns.</summary>
internal static class PackArchivePlanner
{
    internal static async Task<PreparedPack> PrepareAsync(AddonPack pack, IReadOnlyList<Addon> catalog,
        IReadOnlyList<Addon> installed, IReadOnlySet<long> ignored, IDictionary<long, string> archives,
        Func<Addon, Task<string>> download, CancellationToken token)
    {
        var metadata = catalog.ToDictionary(item => item.CatalogId!.Value);
        var inspected = new HashSet<long>();
        var failed = new HashSet<long>();
        var errors = new List<string>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var available = metadata.Values.Where(item => !failed.Contains(item.CatalogId!.Value)).ToArray();
            var plan = PackPlanner.Build(pack, available, installed, ignored);
            var next = plan.Plan?.Packages.FirstOrDefault(item => !inspected.Contains(item.CatalogId!.Value));
            if (next is null)
                return new(plan with { Review = plan.Review.Concat(errors).ToArray() }, metadata.Values.ToArray());
            if (inspected.Count >= 100) throw new InvalidDataException(UiText.Get("Слишком много пакетов зависимостей."));
            var id = next.CatalogId!.Value;
            inspected.Add(id);
            try
            {
                // Повторно используем ZIP только при совпадении с контрольной суммой свежего каталога.
                // При отсутствии суммы скачиваем заново, а не доверяем прежнему временному файлу.
                if (!archives.TryGetValue(id, out var path) || !await MatchesAsync(next, path, token))
                {
                    path = await download(next);
                    archives[id] = path;
                }
                if (next.CatalogChecksum.Length == 32 && !await MatchesAsync(next, path, token))
                    throw new InvalidDataException("Контрольная сумма архива не совпала. Повторите скачивание.");
                metadata[id] = await Task.Run(() => ArchiveDependencyPlanner.Inspect(next, path, token), token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
                or ArgumentException or InvalidOperationException or NotSupportedException or CryptographicException
                || error is OperationCanceledException && !token.IsCancellationRequested)
            {
                AppLog.Error("pack.archive.failed", error);
                failed.Add(id);
                errors.Add(next.Name + ": " + UiError.Describe(error));
            }
        }
    }

    private static async Task<bool> MatchesAsync(Addon addon, string path, CancellationToken token)
    {
        if (addon.CatalogChecksum.Length != 32 || !File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        var checksum = Convert.ToHexString(await MD5.HashDataAsync(file, token));
        return checksum.Equals(addon.CatalogChecksum, StringComparison.OrdinalIgnoreCase);
    }
}
