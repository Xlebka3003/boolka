using System.IO;
using System.Text.Json;

namespace Boolka;

/// <summary>Перемещает только выбранную папку аддона в резервную копию; настройки игры не удаляются.</summary>
public sealed class SafeAddonRemoval
{
    private readonly Action<string>? checkpoint;
    public SafeAddonRemoval() { }
    internal SafeAddonRemoval(Action<string> checkpoint) => this.checkpoint = checkpoint;
    private sealed record RemovalJournal(string Target, string State, IReadOnlyList<string> Folders, IReadOnlyList<string> PreviouslyPresent);

    public Task<InstallResult> RemoveAsync(string addonFolder, string selectedAddonFolder, CancellationToken token)
        => Task.Run(() => Remove(addonFolder, selectedAddonFolder, token));

    private InstallResult Remove(string addonFolder, string selectedAddonFolder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(addonFolder) || !Path.IsPathFullyQualified(selectedAddonFolder))
            throw new ArgumentException("Нужны полные пути папки AddOns и выбранного аддона.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonFolder));
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedAddonFolder));
        var name = Path.GetFileName(selected);
        if (!Directory.Exists(root) || !Path.GetFileName(root).Equals("AddOns", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выберите существующую папку AddOns.");
        if (!string.Equals(Path.GetDirectoryName(selected), root, StringComparison.OrdinalIgnoreCase)
            || !SafeInstaller.IsSafeRelativePath(name) || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("Удаление разрешено только для одной непосредственной папки внутри AddOns.");
        CheckAncestors(root); CheckAncestors(selected);
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("Выбранная папка аддона не найдена.");
        var parent = Directory.GetParent(root)?.FullName ?? throw new IOException("Нельзя использовать корень диска.");
        var transactions = StorageMigration.Transactions(parent);
        CheckAncestors(transactions);
        if (File.Exists(transactions)) throw new IOException("Путь резервных копий занят файлом.");
        Directory.CreateDirectory(transactions); CheckAncestors(transactions);
        var lockPath = Path.Combine(transactions, "install.lock");
        if (File.Exists(lockPath)) CheckFile(lockPath);
        using var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckPending(transactions, root);
        CheckTree(selected); CheckManifest(selected, name);
        token.ThrowIfCancellationRequested();
        var operation = Path.Combine(transactions, Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(operation, "backup");
        Directory.CreateDirectory(backup); CheckAncestors(backup);
        var destination = Path.Combine(backup, name);
        var journalPath = Path.Combine(operation, "pending.json");
        // Журнал синхронно записывается до перемещения. Отмена не прерывает фиксацию или откат.
        WriteJournal(journalPath, new(root, "removing", new[] { name }, new[] { name }));
        var moved = false;
        try
        {
            checkpoint?.Invoke("before-move");
            CheckAncestors(root); CheckTree(selected); CheckManifest(selected, name); CheckAncestors(backup);
            Directory.Move(selected, destination); moved = true;
            checkpoint?.Invoke("after-move");
            File.Move(journalPath, Path.Combine(operation, "completed.json"));
            // Используется общий результат операций над папками; список содержит только затронутую папку.
            return new(backup, new[] { name });
        }
        catch (Exception failure)
        {
            try
            {
                if (moved)
                {
                    checkpoint?.Invoke("before-rollback");
                    CheckAncestors(root); CheckTree(destination);
                    if (Directory.Exists(selected) || File.Exists(selected)) throw new IOException("Исходное место занято; автоматический откат остановлен.");
                    Directory.Move(destination, selected);
                }
                File.Move(journalPath, Path.Combine(operation, "rolled-back.json"));
            }
            catch (Exception rollback)
            {
                throw new IOException($"Удаление прервано, восстановление не завершено. Ничего не удаляйте. Журнал и резервная копия: {operation}", new AggregateException(failure, rollback));
            }
            throw;
        }
    }

    private static void CheckPending(string directory, string target)
    {
        foreach (var operation in Directory.EnumerateDirectories(directory))
        {
            CheckAncestors(operation);
            var pending = Path.Combine(operation, "pending.json");
            if (!File.Exists(pending)) continue;
            CheckFile(pending);
            try
            {
                using var file = new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 128 * 1024) throw new InvalidDataException("Слишком большой журнал.");
                using var journal = JsonDocument.Parse(file);
                var root = journal.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Target", out var path) || path.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(path.GetString()) || !Path.IsPathFullyQualified(path.GetString()!)
                    || !root.TryGetProperty("Folders", out var folders) || folders.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("PreviouslyPresent", out var previous) || previous.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Неверный формат журнала.");
                if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.GetString()!)).Equals(target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Есть незавершённая операция. Проверьте журнал и резервные копии: {operation}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            { throw new IOException($"Повреждён журнал операции. Требуется ручная проверка: {operation}", ex); }
        }
    }

    private static void CheckManifest(string directory, string name)
    {
        var manifest = new[] { Path.Combine(directory, name + ".txt"), Path.Combine(directory, name + ".addon") }.FirstOrDefault(File.Exists);
        if (manifest is null) throw new InvalidDataException("В выбранной папке нет соответствующего манифеста аддона.");
        CheckFile(manifest);
        using var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Некорректный размер манифеста.");
        using var reader = new StreamReader(file);
        var valid = false;
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("##") || !line.Contains(':')) continue;
            var pair = line[2..].Split(':', 2);
            if (pair[1].Trim().Length == 0) continue;
            if (new[] { "Title", "APIVersion", "Version", "AddOnVersion" }.Contains(pair[0].Trim(), StringComparer.OrdinalIgnoreCase)) { valid = true; break; }
        }
        if (!valid) throw new InvalidDataException("В выбранном манифесте не найдены сведения об аддоне.");
    }
    private static void CheckTree(string directory)
    {
        CheckAncestors(directory); var pending = new Stack<string>(); pending.Push(directory); var count = 0;
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 100000) throw new IOException("Слишком много файлов для безопасной проверки аддона.");
                if (Path.GetFileName(entry).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) throw new IOException("В папке есть SavedVariables. Автоматическое удаление запрещено.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки внутри аддона запрещены.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }
    private static void CheckAncestors(string path)
    { for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent) if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папки-ссылки запрещены: " + directory.FullName); }
    private static void CheckFile(string path)
    { CheckAncestors(Path.GetDirectoryName(path)!); if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Файлы-ссылки запрещены: " + path); }
    private static void WriteJournal(string path, RemovalJournal journal)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(file, journal); file.Flush(true); }
}
