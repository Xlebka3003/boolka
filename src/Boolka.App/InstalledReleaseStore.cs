using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boolka;

public sealed record VerifiedInstalledRelease(long Id, string Version);

public static class InstalledReleaseStore
{
    private sealed record Receipt(string Target, IReadOnlyList<Package> Packages);
    private sealed record Package(long Id, string Version, IReadOnlyList<string> Folders, IReadOnlyList<ContentFile> Files);
    private sealed record ContentFile(string Path, long Length, string Sha256);
    private const int MaxJsonBytes = 16 * 1024 * 1024;

    public static IReadOnlyDictionary<string, VerifiedInstalledRelease> LoadVerified(string addonFolder)
    {
        var verified = new Dictionary<string, VerifiedInstalledRelease>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Path.IsPathFullyQualified(addonFolder)) return verified;
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonFolder));
            CheckAncestors(target);
            var parent = Directory.GetParent(target);
            if (parent is null) return verified;
            var transactions = StorageMigration.Transactions(parent.FullName);
            if (!Directory.Exists(transactions)) return verified;
            CheckAncestors(transactions);
            var operations = Directory.EnumerateDirectories(transactions).Take(257).ToList();
            if (operations.Count > 256) return verified;
            long metadataBytes = 0, hashedBytes = 0;
            var candidates = new List<(string Path, DateTime Time)>();
            foreach (var operation in operations)
            {
                CheckAncestors(operation);
                var completed = Path.Combine(operation, "completed.json");
                if (File.Exists(completed)) candidates.Add((operation, File.GetLastWriteTimeUtc(completed)));
            }
            foreach (var candidate in candidates.OrderByDescending(x => x.Time))
            {
                try
                {
                    var completed = ReadDocument(Path.Combine(candidate.Path, "completed.json"), 128 * 1024, ref metadataBytes);
                    using (completed)
                    {
                        var journal = completed.RootElement;
                        if (journal.ValueKind != JsonValueKind.Object || !journal.TryGetProperty("Target", out var journalTarget)
                            || journalTarget.ValueKind != JsonValueKind.String || !SameTarget(journalTarget.GetString(), target)
                            || !journal.TryGetProperty("Folders", out var folderValues) || folderValues.ValueKind != JsonValueKind.Array) continue;
                        var journalFolders = folderValues.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var receiptPath = Path.Combine(candidate.Path, "releases.json");
                        if (!File.Exists(receiptPath)) continue;
                        using var document = ReadDocument(receiptPath, MaxJsonBytes, ref metadataBytes);
                        var receipt = document.Deserialize<Receipt>();
                        if (receipt is null || !SameTarget(receipt.Target, target) || receipt.Packages is null || receipt.Packages.Count is 0 or > 100) continue;
                        foreach (var package in receipt.Packages)
                        {
                            if (!Valid(package) || package.Folders.Any(x => !journalFolders.Contains(x))
                                || package.Folders.All(verified.ContainsKey)) continue;
                            if (!Matches(package, target, ref hashedBytes)) continue;
                            foreach (var root in package.Folders) verified.TryAdd(root, new(package.Id, package.Version));
                        }
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex)) { }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex)) { }
        return verified;
    }

    internal static void Write(string operation, string target, IReadOnlyList<Addon> packages, string stage, CancellationToken token)
    {
        var records = new List<Package>();
        long total = 0;
        foreach (var package in packages)
        {
            if (package.CatalogId is not > 0 || string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 128)
                throw new InvalidDataException("Нет номера проверенного выпуска для квитанции установки.");
            var roots = package.PackageModules.Select(x => x.Path.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var files = Walk(stage, roots).Select(path =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = OpenChecked(path);
                total += stream.Length;
                if (total > SafeInstaller.MaxExpandedBytes) throw new InvalidDataException("Квитанция превышает предел размера установки.");
                return new ContentFile(Path.GetRelativePath(stage, path).Replace('\\', '/'), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }).ToArray();
            var record = new Package(package.CatalogId.Value, package.Version, roots, files);
            if (!Valid(record)) throw new InvalidDataException("Некорректный состав квитанции установки.");
            records.Add(record);
        }
        using var file = new FileStream(Path.Combine(operation, "releases.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new Receipt(Path.GetFullPath(target), records));
        if (file.Length > MaxJsonBytes) throw new InvalidDataException("Квитанция установки слишком велика.");
        file.Flush(true);
    }

    private static bool Matches(Package package, string target, ref long hashedBytes)
    {
        var expected = package.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var actual = Walk(target, package.Folders);
        if (actual.Count != expected.Count) return false;
        foreach (var path in actual)
        {
            if (!expected.TryGetValue(Path.GetRelativePath(target, path).Replace('\\', '/'), out var entry)) return false;
            using var stream = OpenChecked(path);
            if (stream.Length != entry.Length) return false;
            hashedBytes += stream.Length;
            if (hashedBytes > SafeInstaller.MaxExpandedBytes) return false;
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static List<string> Walk(string target, IReadOnlyList<string> roots)
    {
        var files = new List<string>(); var pending = new Stack<string>(); var entries = 0;
        foreach (var root in roots)
        {
            if (!SafeInstaller.IsSafeRelativePath(root) || root.Contains('/') || root.Contains('\\')) throw new InvalidDataException("Недопустимый корень квитанции.");
            var path = Path.Combine(target, root); CheckAncestors(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
            pending.Push(path);
        }
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++entries > 50000 || !SafeInstaller.IsSafeRelativePath(Path.GetRelativePath(target, path))) throw new InvalidDataException("Состав выпуска не подтверждён.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки запрещены при проверке выпуска.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path); else files.Add(path);
            }
        return files;
    }

    private static bool Valid(Package? package) => package is { Id: > 0, Version: not null, Folders: not null, Files: not null }
        && package.Version.Length is > 0 and <= 128 && package.Folders.Count is > 0 and <= 50000 && package.Files.Count is > 0 and <= 50000
        && package.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == package.Folders.Count
        && package.Folders.All(x => x is not null && SafeInstaller.IsSafeRelativePath(x) && !x.Contains('/') && !x.Contains('\\'))
        && package.Files.All(x => x is not null && x.Path is not null && SafeInstaller.IsSafeRelativePath(x.Path)
            && package.Folders.Contains(x.Path.Split('/')[0], StringComparer.OrdinalIgnoreCase)
            && x.Length is >= 0 and <= SafeInstaller.MaxFileBytes && x.Sha256 is { Length: 64 } && x.Sha256.All(Uri.IsHexDigit))
        && package.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == package.Files.Count;
    private static JsonDocument ReadDocument(string path, int maximum, ref long total)
    {
        using var stream = OpenChecked(path); total += stream.Length;
        if (stream.Length > maximum || total > 32 * 1024 * 1024) throw new InvalidDataException("Превышен предел чтения квитанций.");
        return JsonDocument.Parse(stream);
    }
    private static FileStream OpenChecked(string path)
    {
        CheckAncestors(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Файл-ссылка запрещён.");
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static void CheckAncestors(string path)
    { for (var info = new DirectoryInfo(path); info is not null; info = info.Parent) if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка-ссылка запрещена."); }
    private static bool SameTarget(string? value, string target) => value is not null && Path.IsPathFullyQualified(value)
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).Equals(target, StringComparison.OrdinalIgnoreCase);
    private static bool IsReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException;
}
