using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Boolka;

internal sealed record ApplicationUpdateFile(string Name, string Sha256);
internal sealed record ApplicationUpdatePlan(string Version, string Target, int ParentId, long ParentStart,
    string PreviousExecutableHash, IReadOnlyList<ApplicationUpdateFile> Files);

internal static class ApplicationUpdatePackage
{
    internal static readonly string[] FileNames =
    [
        "boolka.exe", "LICENSE", "THIRD_PARTY_NOTICES.txt", "Assets/Flags/LICENSE.txt", "Assets/Lucide/LICENSE.txt",
        "Licenses/dotnet/Desktop-LICENSE.txt", "Licenses/dotnet/Runtime-LICENSE.txt", "Licenses/dotnet/Runtime-THIRD-PARTY-NOTICES.txt"
    ];
    internal static string CacheRoot => Path.Combine(Path.GetTempPath(), "boolka-app-update");
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    internal static string CreateWorkspace()
    {
        CheckPath(CacheRoot);
        Directory.CreateDirectory(CacheRoot);
        var path = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string ValidateWorkspace(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        if (!string.Equals(Path.GetDirectoryName(workspace), Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _)) throw new InvalidDataException("Недопустимая временная папка обновления.");
        CheckPath(workspace);
        return workspace;
    }

    internal static ApplicationUpdatePlan Prepare(string archive, string workspace, string target, Version version, int parentId, long parentStart, string archiveSha256)
    {
        workspace = ValidateWorkspace(workspace);
        target = Path.GetFullPath(target);
        CheckPath(target);
        if (!Directory.Exists(target) || target.Equals(workspace, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимая папка приложения.");
        foreach (var name in FileNames) CheckPath(Path.Combine(target, name));
        var oldExecutable = Path.Combine(target, "boolka.exe");
        CheckPath(oldExecutable);
        if (!File.Exists(oldExecutable)) throw new InvalidDataException("Не найден установленный файл boolka.exe.");
        var payload = Path.Combine(workspace, "payload");
        if (Directory.Exists(payload)) throw new InvalidDataException("Временная папка уже использована.");
        Directory.CreateDirectory(payload);
        CheckPath(archive);
        // Хеш и извлечение читают один открытый файл: архив нельзя подменить между этими шагами.
        using var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(archiveStream)).Equals(archiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Архив изменился после скачивания.");
        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 32) throw new InvalidDataException("Слишком много файлов в обновлении.");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки в архиве обновления запрещены.");
            if (name.EndsWith('/'))
            {
                if (!FileNames.Any(file => file.StartsWith(name, StringComparison.Ordinal))) throw new InvalidDataException("Неизвестная папка обновления.");
                continue;
            }
            if (!FileNames.Contains(name, StringComparer.Ordinal) || !found.Add(name) || entry.Length < 0)
                throw new InvalidDataException("Неизвестный или повторный файл обновления.");
            total = checked(total + entry.Length);
            if (total > 300L * 1024 * 1024 || entry.Length > ApplicationReleaseClient.MaximumDownload)
                throw new InvalidDataException("Превышен распакованный размер обновления.");
            var destination = Path.Combine(payload, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                written = checked(written + read);
                if (written > entry.Length) throw new InvalidDataException("Размер файла ZIP отличается от заголовка.");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("Неполный файл в архиве обновления.");
        }
        if (found.Count != FileNames.Length) throw new InvalidDataException("Неполный набор файлов обновления.");
        ValidateExecutable(Path.Combine(payload, "boolka.exe"), version);
        var files = FileNames.Select(name => new ApplicationUpdateFile(name, Hash(Path.Combine(payload, name)))).ToArray();
        var plan = new ApplicationUpdatePlan(version.ToString(), target, parentId, parentStart, Hash(oldExecutable), files);
        WriteNewText(Path.Combine(workspace, "plan.json"), JsonSerializer.Serialize(plan, JsonOptions));
        return plan;
    }

    internal static ApplicationUpdatePlan ReadPlan(string planFile)
    {
        var workspace = ValidateWorkspace(Path.GetDirectoryName(Path.GetFullPath(planFile))!);
        if (Path.GetFileName(planFile) != "plan.json") throw new InvalidDataException("Неверное имя плана обновления.");
        CheckPath(planFile);
        if (new FileInfo(planFile).Length > 32768) throw new InvalidDataException("Слишком большой план обновления.");
        var plan = JsonSerializer.Deserialize<ApplicationUpdatePlan>(File.ReadAllText(Path.Combine(workspace, "plan.json")), JsonOptions)
            ?? throw new InvalidDataException("Пустой план обновления.");
        if (string.IsNullOrWhiteSpace(plan.Target) || !Path.IsPathFullyQualified(plan.Target) || string.IsNullOrWhiteSpace(plan.Version)
            || plan.Files is null || plan.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Name))
            || string.IsNullOrEmpty(plan.PreviousExecutableHash)) throw new InvalidDataException("Повреждён план обновления.");
        return plan;
    }

    internal static void ValidateExecutable(string path, Version expected)
    {
        CheckPath(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart != expected.Major || info.FileMinorPart != expected.Minor || info.FileBuildPart != expected.Build
            || info.FilePrivatePart != 0 || info.OriginalFilename != "boolka.dll")
            throw new InvalidDataException("В архиве неверная версия или другой исполняемый файл.");
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("Неверный формат EXE.");
    }

    internal static string Hash(string path)
    {
        CheckPath(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteNewText(string path, string text)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
        stream.Flush(true);
    }

    internal static void CheckPath(string path)
    {
        // Проверяем также родителей: запрет ссылки только на последнем компоненте недостаточен.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в путях обновления запрещены.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
