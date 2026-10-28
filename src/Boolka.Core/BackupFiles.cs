using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boolka;

internal sealed record BackupFile(string Path, long Size, string Sha256);
internal sealed record BackupManifest(int Format, string Target, DateTimeOffset Created, string Reason,
    string[] Roots, string[] Directories, BackupFile[] Files);

internal static class BackupFiles
{
    internal const long MaxBytes = 20L * 1024 * 1024 * 1024;
    internal const int MaxEntries = 100_000;

    internal static string Target(string folder)
    {
        if (!System.IO.Path.IsPathFullyQualified(folder)) throw new IOException("Нужен полный путь AddOns.");
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        if (!System.IO.Path.GetFileName(full).Equals("AddOns", StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите папку AddOns.");
        Check(full);
        return full;
    }

    internal static void Check(string path)
    {
        // Проверяем также родителей: переход по junction/symlink не должен выводить операцию за выбранные папки.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Папки и файлы-ссылки не поддерживаются: " + current.FullName);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string Inside(string root, string relative)
    {
        if (relative is null || relative.Length is 0 or > 1024 || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/'))
            throw new InvalidDataException("Недопустимый путь в резервной копии.");
        foreach (var part in relative.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9')
                throw new InvalidDataException("Недопустимый путь в резервной копии.");
        }
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar, FileSystemPaths.Comparison))
            throw new InvalidDataException("Путь выходит из папки резервной копии.");
        Check(full);
        return full;
    }

    internal static (string[] Directories, string[] Files) Walk(string root)
    {
        Check(root);
        if (!Directory.Exists(root)) return ([], []);
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                Check(path);
                if (directories.Count + files.Count >= MaxEntries) throw new IOException("В копии слишком много файлов.");
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                _ = Inside(root, relative);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(path); }
                else files.Add(relative);
            }
        }
        return (directories.Order(StringComparer.OrdinalIgnoreCase).ToArray(), files.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static BackupFile Copy(string source, string destination, string relative, CancellationToken token)
    {
        Check(source); Check(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxBytes) throw new IOException("Файл слишком большой для резервной копии.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        Check(destination);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long size = 0;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if ((size += count) > MaxBytes) throw new IOException("Файл слишком большой для резервной копии.");
            output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count);
        }
        output.Flush(true);
        return new(relative, size, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static void WriteJson<T>(string path, T value)
    {
        Check(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value);
        file.Flush(true);
    }

    internal static T ReadJson<T>(string path, int maximum) where T : class
    {
        Check(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Слишком большой файл сведений резервной копии.");
        return JsonSerializer.Deserialize<T>(file) ?? throw new InvalidDataException("Повреждены сведения резервной копии.");
    }
}
