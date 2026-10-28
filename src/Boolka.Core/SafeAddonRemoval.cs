using System.IO;
using System.Text.Json;

namespace Boolka;

/// <summary>Перемещает только выбранные папки аддонов в резервную копию; настройки игры не удаляются.</summary>
public sealed class SafeAddonRemoval
{
    private readonly Action<string>? checkpoint;
    public SafeAddonRemoval() { }
    internal SafeAddonRemoval(Action<string> checkpoint) => this.checkpoint = checkpoint;
    private sealed record RemovalJournal(string Target, string State, IReadOnlyList<string> Folders, IReadOnlyList<string> PreviouslyPresent);

    public Task<InstallResult> RemoveAsync(string addonFolder, string selectedAddonFolder, CancellationToken token)
        => RemoveAsync(addonFolder, new[] { selectedAddonFolder }, token);

    public Task<InstallResult> RemoveAsync(string addonFolder, IReadOnlyList<string> selectedAddonFolders, CancellationToken token)
        => Task.Run(() => Remove(addonFolder, selectedAddonFolders, token));

    private InstallResult Remove(string addonFolder, IReadOnlyList<string> selectedAddonFolders, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(addonFolder) || selectedAddonFolders.Count is 0 or > 1000
            || selectedAddonFolders.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Нужны полные пути AddOns и от 1 до 1000 выбранных папок.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonFolder));
        var selected = selectedAddonFolders.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(FileSystemPaths.Comparer).ToArray();
        if (!Directory.Exists(root) || !Path.GetFileName(root).Equals("AddOns", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выберите существующую папку AddOns.");
        CheckAncestors(root);
        foreach (var path in selected)
        {
            var name = Path.GetFileName(path);
            if (!string.Equals(Path.GetDirectoryName(path), root, FileSystemPaths.Comparison)
                || !SafeInstaller.IsSafeRelativePath(name) || name.Contains('/') || name.Contains('\\'))
                throw new InvalidDataException("Удаление разрешено только для непосредственных папок внутри AddOns.");
            CheckAncestors(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Выбранная папка аддона не найдена.");
        }
        var parent = Directory.GetParent(root)?.FullName ?? throw new IOException("Нельзя использовать корень диска.");
        var transactions = StorageMigration.Transactions(parent);
        CheckAncestors(transactions);
        if (File.Exists(transactions)) throw new IOException("Путь резервных копий занят файлом.");
        Directory.CreateDirectory(transactions); CheckAncestors(transactions);
        var lockPath = Path.Combine(transactions, "install.lock");
        if (File.Exists(lockPath)) CheckFile(lockPath);
        using var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckPending(transactions, root);
        foreach (var path in selected) { CheckTree(path); CheckManifest(path, Path.GetFileName(path)); }
        token.ThrowIfCancellationRequested();
        var operation = Path.Combine(transactions, Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(operation, "backup");
        Directory.CreateDirectory(backup); CheckAncestors(backup);
        var names = selected.Select(Path.GetFileName).Select(name => name!).ToArray();
        var journalPath = Path.Combine(operation, "pending.json");
        // Журнал синхронно записывается до перемещения. Отмена не прерывает фиксацию или откат.
        WriteJournal(journalPath, new(root, "removing", names, names));
        var moved = new List<string>();
        try
        {
            foreach (var path in selected)
            {
                checkpoint?.Invoke("before-move");
                CheckAncestors(root); CheckTree(path); CheckManifest(path, Path.GetFileName(path)); CheckAncestors(backup);
                Directory.Move(path, Path.Combine(backup, Path.GetFileName(path)));
                moved.Add(path);
                checkpoint?.Invoke("after-move");
            }
            File.Move(journalPath, Path.Combine(operation, "completed.json"));
            // Общая резервная копия содержит все папки выбранного набора.
            return new(backup, names);
        }
        catch (Exception failure)
        {
            try
            {
                // Весь выбранный набор — одна операция: при сбое возвращаем уже перемещённые папки.
                foreach (var path in moved.AsEnumerable().Reverse())
                {
                    checkpoint?.Invoke("before-rollback");
                    var destination = Path.Combine(backup, Path.GetFileName(path));
                    CheckAncestors(root); CheckTree(destination);
                    if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Исходное место занято; автоматический откат остановлен.");
                    Directory.Move(destination, path);
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
                if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.GetString()!)).Equals(target, FileSystemPaths.Comparison))
                    throw new IOException($"Есть незавершённая операция. Проверьте журнал и резервные копии: {operation}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            { throw new IOException($"Повреждён журнал операции. Требуется ручная проверка: {operation}", ex); }
        }
    }

    private static void CheckManifest(string directory, string name, bool allowContainer = true)
    {
        var manifest = new[] { Path.Combine(directory, name + ".txt"), Path.Combine(directory, name + ".addon") }.FirstOrDefault(File.Exists);
        if (manifest is null)
        {
            if (!allowContainer) throw new InvalidDataException("В выбранной папке нет соответствующего манифеста аддона.");
            // Сканер и установщик поддерживают пакеты с вложенными модулями без корневого
            // манифеста. Для удаления требуем полный обход и валидный манифест каждого модуля.
            var scan = LocalModuleScanner.Scan(directory, new(name, "", []));
            var modules = scan.Modules.Skip(1).ToArray();
            if (!scan.Complete || modules.Length == 0)
                throw new InvalidDataException("В выбранной папке нет соответствующего манифеста аддона.");
            foreach (var module in modules)
            {
                if (!SafeInstaller.IsSafeRelativePath(module.Path)
                    || !module.Path.StartsWith(name + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("В выбранной папке нет соответствующего манифеста аддона.");
                var relative = module.Path[(name.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
                var nested = Path.GetFullPath(Path.Combine(directory, relative));
                if (!nested.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, FileSystemPaths.Comparison))
                    throw new InvalidDataException("Удаление разрешено только для непосредственных папок внутри AddOns.");
                CheckManifest(nested, Path.GetFileName(nested), allowContainer: false);
            }
            return;
        }
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
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > 128 * 1024) throw new InvalidDataException("Слишком большой журнал удаления.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
    }
}
