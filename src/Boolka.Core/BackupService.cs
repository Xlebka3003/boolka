using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boolka;

public sealed record BackupInfo(string Id, DateTimeOffset Created, long Bytes, int Files, string Reason, string? Error = null)
{
    public bool IsValid => Error is null;
    public override string ToString()
    {
        if (!IsValid) return $"{Id[..8]} · {UiText.Get("Повреждённая копия")}";
        var size = Bytes < 1024 ? $"{Bytes} B" : Bytes < 1048576 ? $"{Bytes / 1024d:N1} KB" : $"{Bytes / 1048576d:N1} MB";
        var reason = UiText.Get(Reason == "before-restore" ? "Перед восстановлением" : "Ручная копия");
        return $"{Created.ToLocalTime():G} · {size} · {Files} {UiText.Get("файлов")} · {reason}";
    }
}

/// <summary>Полные локальные снимки. Оригиналы при восстановлении перемещаются в журнал, а не удаляются.</summary>
public sealed class BackupService
{
    private static readonly string[] RootNames = ["AddOns", "SavedVariables"];
    private readonly Action<string>? checkpoint;
    private readonly Action ensureGameClosed = EnsureGameClosed;
    public BackupService() { }
    internal BackupService(Action<string> checkpoint, Action? ensureGameClosed = null)
    { this.checkpoint = checkpoint; this.ensureGameClosed = ensureGameClosed ?? EnsureGameClosed; }
    private sealed record RestoreJournal(string Kind, string Target, string[] Folders, string[] PreviouslyPresent, string SafetyBackup, string[] Desired);

    public static string StorageFolder(string addonFolder) => Path.Combine(Path.GetDirectoryName(BackupFiles.Target(addonFolder))!, ".boolka-backups");
    private static string Transactions(string target) => StorageMigration.Transactions(Path.GetDirectoryName(target)!);
    private static string SnapshotPath(string storage, string id)
    {
        if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Недопустимый идентификатор копии.");
        return BackupFiles.Inside(storage, id);
    }

    public Task<IReadOnlyList<BackupInfo>> ListAsync(string folder, CancellationToken token = default) => Task.Run<IReadOnlyList<BackupInfo>>(() =>
    {
        var target = BackupFiles.Target(folder);
        var storage = StorageFolder(target);
        BackupFiles.Check(storage);
        if (!Directory.Exists(storage)) return [];
        var result = new List<BackupInfo>();
        foreach (var directory in Directory.EnumerateDirectories(storage))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= 1000) throw new IOException("Слишком много резервных копий.");
            var id = Path.GetFileName(directory);
            if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigit)) continue;
            try { result.Add(Info(id, ReadManifest(directory, target))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { result.Add(new(id, DateTimeOffset.MinValue, 0, 0, "invalid", ex.Message)); }
        }
        return result.OrderByDescending(x => x.Created).ToArray();
    }, token);

    public Task<BackupInfo> CreateAsync(string folder, CancellationToken token = default) => Task.Run(() =>
    {
        var target = BackupFiles.Target(folder);
        ensureGameClosed();
        using var operationLock = AcquireLock(target);
        CheckPending(target);
        return Create(target, "manual", token);
    }, token);

    private static BackupInfo Info(string id, BackupManifest manifest) => new(id, manifest.Created, manifest.Files.Sum(x => x.Size), manifest.Files.Length, manifest.Reason);

    private BackupInfo Create(string target, string reason, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Папка AddOns не найдена.");
        var storage = StorageFolder(target);
        BackupFiles.Check(storage);
        Directory.CreateDirectory(storage);
        BackupFiles.Check(storage);
        AppLog.Info("backup.create.begin");
        var id = Guid.NewGuid().ToString("N");
        var temporary = BackupFiles.Inside(storage, "creating-" + id);
        var data = Path.Combine(temporary, "data");
        Directory.CreateDirectory(data);
        var directories = new List<string>();
        var files = new List<BackupFile>();
        var roots = new List<string>();
        long bytes = 0;
        foreach (var root in RootNames)
        {
            token.ThrowIfCancellationRequested();
            var source = Path.Combine(parent, root);
            BackupFiles.Check(source);
            if (File.Exists(source)) throw new IOException("Папка занята файлом: " + source);
            if (!Directory.Exists(source)) continue;
            roots.Add(root); directories.Add(root);
            Directory.CreateDirectory(BackupFiles.Inside(data, root));
            var tree = BackupFiles.Walk(source);
            foreach (var directory in tree.Directories)
            {
                var relative = root + "/" + directory;
                directories.Add(relative);
                Directory.CreateDirectory(BackupFiles.Inside(data, relative));
            }
            foreach (var file in tree.Files)
            {
                token.ThrowIfCancellationRequested();
                var relative = root + "/" + file;
                var entry = BackupFiles.Copy(BackupFiles.Inside(source, file), BackupFiles.Inside(data, relative), relative, token);
                files.Add(entry);
                if ((bytes += entry.Size) > BackupFiles.MaxBytes || files.Count + directories.Count > BackupFiles.MaxEntries)
                    throw new IOException("Превышен размер или число файлов резервной копии.");
            }
            // Не публикуем неполный снимок, если содержимое папок изменилось во время копирования.
            var after = BackupFiles.Walk(source);
            if (!tree.Directories.SequenceEqual(after.Directories) || !tree.Files.SequenceEqual(after.Files))
                throw new IOException("Состав файлов изменился во время создания копии. Закройте игру и повторите.");
        }
        var manifest = new BackupManifest(1, target, DateTimeOffset.UtcNow, reason, roots.ToArray(), directories.ToArray(), files.ToArray());
        AppLog.Info("backup.create.copied");
        BackupFiles.WriteJson(Path.Combine(temporary, "manifest.json"), manifest);
        Verify(temporary, manifest, token);
        AppLog.Info("backup.create.verified");
        ensureGameClosed();
        BackupFiles.Check(temporary); BackupFiles.Check(storage);
        Directory.Move(temporary, SnapshotPath(storage, id));
        AppLog.Info("backup.create.published");
        return Info(id, manifest);
    }

    private static BackupManifest ReadManifest(string directory, string target)
    {
        var manifest = BackupFiles.ReadJson<BackupManifest>(Path.Combine(directory, "manifest.json"), 32 * 1024 * 1024);
        if (manifest.Format != 1 || !string.Equals(manifest.Target, target, FileSystemPaths.Comparison)
            || manifest.Roots is null || manifest.Directories is null || manifest.Files is null || manifest.Reason is not ("manual" or "before-restore")
            || manifest.Roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Roots.Length || !manifest.Roots.Contains("AddOns")
            || manifest.Roots.Any(x => !RootNames.Contains(x)) || manifest.Directories.Length + manifest.Files.Length > BackupFiles.MaxEntries)
            throw new InvalidDataException("Копия повреждена или относится к другой папке AddOns.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var data = Path.Combine(directory, "data");
        long bytes = 0;
        foreach (var path in manifest.Directories.Concat(manifest.Files.Select(x => x?.Path ?? "")))
        {
            _ = BackupFiles.Inside(data, path);
            if (!manifest.Roots.Contains(path.Split('/')[0]) || !paths.Add(path)) throw new InvalidDataException("Повторный путь в резервной копии.");
        }
        if (manifest.Roots.Any(x => !manifest.Directories.Contains(x))) throw new InvalidDataException("В копии отсутствует корневая папка.");
        foreach (var file in manifest.Files)
        {
            if (file.Size < 0 || file.Size > BackupFiles.MaxBytes || (bytes += file.Size) > BackupFiles.MaxBytes
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Повреждены сведения о файле резервной копии.");
        }
        return manifest;
    }

    private static void Verify(string directory, BackupManifest manifest, CancellationToken token)
    {
        VerifyData(Path.Combine(directory, "data"), manifest, token);
    }

    private static void VerifyData(string data, BackupManifest manifest, CancellationToken token)
    {
        var tree = BackupFiles.Walk(data);
        if (!tree.Directories.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(manifest.Directories.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            || !tree.Files.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(manifest.Files.Select(x => x.Path).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Состав файлов копии не совпадает со списком.");
        foreach (var entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(BackupFiles.Inside(data, entry.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != entry.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Повреждён файл резервной копии: " + entry.Path);
        }
    }

    public Task RestoreAsync(string folder, string id, CancellationToken token = default) => Task.Run(() =>
    {
        var target = BackupFiles.Target(folder);
        ensureGameClosed();
        using var operationLock = AcquireLock(target);
        CheckPending(target);
        AppLog.Info("backup.restore.begin");
        var snapshot = SnapshotPath(StorageFolder(target), id);
        var manifest = ReadManifest(snapshot, target);
        Verify(snapshot, manifest, token);
        var safety = Create(target, "before-restore", token);
        var transaction = BackupFiles.Inside(Transactions(target), Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(transaction, "stage");
        Directory.CreateDirectory(stage);
        foreach (var directory in manifest.Directories) Directory.CreateDirectory(BackupFiles.Inside(stage, directory));
        foreach (var file in manifest.Files)
        {
            var copy = BackupFiles.Copy(BackupFiles.Inside(Path.Combine(snapshot, "data"), file.Path), BackupFiles.Inside(stage, file.Path), file.Path, token);
            if (!copy.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Копия изменилась при подготовке восстановления.");
        }
        VerifyData(stage, manifest, token);
        var parent = Path.GetDirectoryName(target)!;
        var previous = RootNames.Where(x => Directory.Exists(Path.Combine(parent, x))).ToArray();
        var journal = new RestoreJournal("boolka-full-restore-1", target, RootNames, previous, safety.Id, manifest.Roots);
        token.ThrowIfCancellationRequested();
        ensureGameClosed();
        BackupFiles.WriteJson(Path.Combine(transaction, "pending.json"), journal);
        // После журнала отмена не прерывает замену: завершаем фиксацию или откат обеих папок.
        try
        {
            var before = Path.Combine(transaction, "before");
            Directory.CreateDirectory(before);
            foreach (var root in RootNames)
            {
                checkpoint?.Invoke("before-" + root);
                ensureGameClosed();
                var destination = Path.Combine(parent, root);
                BackupFiles.Check(destination); BackupFiles.Check(stage); BackupFiles.Check(before);
                if (File.Exists(destination)) throw new IOException("Путь папки занят файлом: " + destination);
                if (Directory.Exists(destination)) { _ = BackupFiles.Walk(destination); Directory.Move(destination, Path.Combine(before, root)); }
                checkpoint?.Invoke("moved-old-" + root);
                if (manifest.Roots.Contains(root)) Directory.Move(Path.Combine(stage, root), destination);
                checkpoint?.Invoke("moved-new-" + root);
            }
            File.Move(Path.Combine(transaction, "pending.json"), Path.Combine(transaction, "completed.json"));
            AppLog.Info("backup.restore.complete");
        }
        catch (Exception failure)
        {
            AppLog.Error("backup.restore.rollback", failure);
            try { Rollback(transaction, journal); }
            catch (Exception rollback) { throw new IOException("Восстановление прервано. Сохранены исходные файлы и журнал: " + transaction, new AggregateException(failure, rollback)); }
            throw;
        }
    }, token);

    private static void Rollback(string transaction, RestoreJournal journal)
    {
        var parent = Path.GetDirectoryName(journal.Target)!;
        var before = Path.Combine(transaction, "before");
        var rejected = Path.Combine(transaction, "rejected");
        BackupFiles.Check(transaction);
        Directory.CreateDirectory(rejected);
        foreach (var root in RootNames.Reverse())
        {
            var destination = Path.Combine(parent, root);
            var original = Path.Combine(before, root);
            BackupFiles.Check(destination); BackupFiles.Check(original);
            if (Directory.Exists(original))
            {
                if (Directory.Exists(destination))
                {
                    _ = BackupFiles.Walk(destination);
                    Directory.Move(destination, Path.Combine(rejected, root + "-" + Guid.NewGuid().ToString("N")));
                }
                Directory.Move(original, destination);
            }
            else if (!journal.PreviouslyPresent.Contains(root) && journal.Desired.Contains(root)
                && !Directory.Exists(Path.Combine(transaction, "stage", root)) && Directory.Exists(destination))
            {
                _ = BackupFiles.Walk(destination);
                Directory.Move(destination, Path.Combine(rejected, root + "-" + Guid.NewGuid().ToString("N")));
            }
            else if (journal.PreviouslyPresent.Contains(root) && !Directory.Exists(destination))
                throw new IOException("Не найдена исходная папка. Проверьте копию: " + journal.SafetyBackup);
        }
        File.Move(Path.Combine(transaction, "pending.json"), Path.Combine(transaction, "rolled-back.json"));
    }

    public Task RecoverAsync(string folder) => Task.Run(() =>
    {
        var target = BackupFiles.Target(folder);
        ensureGameClosed();
        using var operationLock = AcquireLock(target);
        foreach (var transaction in Directory.EnumerateDirectories(Transactions(target)))
        {
            var pending = Path.Combine(transaction, "pending.json");
            if (!File.Exists(pending)) continue;
            var journal = BackupFiles.ReadJson<RestoreJournal>(pending, 128 * 1024);
            if (journal.Kind != "boolka-full-restore-1" || !string.Equals(journal.Target, target, FileSystemPaths.Comparison))
                throw new IOException("Обнаружена незавершённая операция другого типа. Проверьте журнал: " + transaction);
            if (journal.Folders is null || !journal.Folders.SequenceEqual(RootNames) || journal.PreviouslyPresent is null || journal.Desired is null
                || !journal.Desired.Contains("AddOns") || journal.PreviouslyPresent.Distinct().Count() != journal.PreviouslyPresent.Length
                || journal.Desired.Distinct().Count() != journal.Desired.Length
                || journal.PreviouslyPresent.Any(x => !RootNames.Contains(x)) || journal.Desired.Any(x => !RootNames.Contains(x)))
                throw new InvalidDataException("Повреждён журнал восстановления.");
            var safety = SnapshotPath(StorageFolder(target), journal.SafetyBackup);
            Verify(safety, ReadManifest(safety, target), CancellationToken.None);
            Rollback(transaction, journal);
        }
    });

    public static bool HasPending(string folder)
    {
        var transactions = Transactions(BackupFiles.Target(folder));
        BackupFiles.Check(transactions);
        return Directory.Exists(transactions) && Directory.EnumerateDirectories(transactions).Any(x => File.Exists(Path.Combine(x, "pending.json")));
    }

    private static FileStream AcquireLock(string target)
    {
        var transactions = Transactions(target);
        BackupFiles.Check(transactions);
        Directory.CreateDirectory(transactions);
        var path = Path.Combine(transactions, "install.lock");
        BackupFiles.Check(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void CheckPending(string target)
    {
        if (HasPending(target)) throw new IOException("Есть незавершённая операция. Сначала выполните возврат прежнего состояния.");
    }

    private static void EnsureGameClosed()
    {
        foreach (var name in new[] { "eso", "eso64", "eso64-steam" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) { if (!process.HasExited) throw new IOException("Закройте ESO перед резервным копированием или восстановлением."); }
    }
}
