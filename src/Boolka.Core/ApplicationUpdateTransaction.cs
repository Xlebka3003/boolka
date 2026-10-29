using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boolka;

internal sealed record ApplicationUpdateOriginal(string Name, string? Sha256);
internal sealed record ApplicationUpdateJournal(string State, IReadOnlyList<ApplicationUpdateFile> Files,
    IReadOnlyList<ApplicationUpdateOriginal> Originals);

internal static class ApplicationUpdateTransaction
{
    private const string Prefix = ".boolka-update-";

    internal static string Apply(ApplicationUpdatePlan plan, string workspace, Action<int>? beforeReplace = null)
    {
        workspace = ApplicationUpdatePackage.ValidateWorkspace(workspace);
        var target = Path.GetFullPath(plan.Target);
        using var targetLock = Lock(target);
        ValidatePlan(plan, workspace);
        RecoverCore(target);
        var previous = Path.Combine(target, "boolka.exe");
        if (ApplicationUpdatePackage.Hash(previous) != plan.PreviousExecutableHash)
            throw new InvalidDataException("Приложение изменилось после подготовки обновления.");
        var old = FileVersionInfo.GetVersionInfo(previous);
        if (new Version(old.FileMajorPart, old.FileMinorPart, old.FileBuildPart) >= ApplicationReleaseClient.ReadVersion(plan.Version))
            throw new InvalidDataException("Повторная установка или понижение приложения запрещены.");
        var transaction = Path.Combine(target, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        File.SetAttributes(transaction, File.GetAttributes(transaction) | FileAttributes.Hidden);
        var originals = new List<ApplicationUpdateOriginal>();
        foreach (var file in plan.Files)
        {
            var destination = Path.Combine(target, file.Name);
            ApplicationUpdatePackage.CheckPath(destination);
            if (Directory.Exists(destination)) throw new InvalidDataException("Вместо файла приложения найдена папка.");
            if (File.Exists(destination))
            {
                // Другой открытый экземпляр блокирует обновление до любых замен.
                using (new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                var hash = ApplicationUpdatePackage.Hash(destination);
                Copy(destination, Path.Combine(transaction, "backup", file.Name));
                if (ApplicationUpdatePackage.Hash(Path.Combine(transaction, "backup", file.Name)) != hash)
                    throw new IOException("Приложение изменилось во время резервного копирования.");
                originals.Add(new(file.Name, hash));
            }
            else originals.Add(new(file.Name, null));
            Copy(Path.Combine(workspace, "payload", file.Name), Path.Combine(transaction, "new", file.Name));
            if (ApplicationUpdatePackage.Hash(Path.Combine(transaction, "new", file.Name)) != file.Sha256)
                throw new InvalidDataException("Повреждён подготовленный файл обновления.");
        }
        var journal = new ApplicationUpdateJournal("prepared", plan.Files, originals);
        Save(transaction, journal);
        try
        {
            var ordered = plan.Files.OrderBy(file => file.Name == "boolka.exe" ? 1 : 0).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var file = ordered[i];
                var destination = Path.Combine(target, file.Name);
                ApplicationUpdatePackage.CheckPath(destination);
                var original = originals.Single(item => item.Name == file.Name);
                if (File.Exists(destination) ? ApplicationUpdatePackage.Hash(destination) != original.Sha256 : original.Sha256 is not null)
                    throw new IOException("Файлы приложения изменились во время обновления.");
                beforeReplace?.Invoke(i);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Подготовленные файлы лежат на том же томе, замена каждого файла атомарна.
                File.Move(Path.Combine(transaction, "new", file.Name), destination, true);
            }
            Save(transaction, journal with { State = "completed" });
            return transaction;
        }
        catch (Exception error)
        {
            try { Rollback(transaction, target, journal); }
            catch (Exception rollback) { throw new AggregateException("Не завершён откат приложения. Резервная копия сохранена: " + transaction, error, rollback); }
            throw;
        }
    }

    internal static void Recover(string target)
    {
        using var targetLock = Lock(target);
        RecoverCore(target);
    }

    internal static bool NeedsRecovery(string target)
    {
        if (!Journals(target).Any(directory => ReadJournal(directory).State == "prepared")) return false;
        using var targetLock = Lock(target);
        return Journals(target).Any(directory => ReadJournal(directory).State == "prepared");
    }

    private static IEnumerable<string> Journals(string target)
    {
        ApplicationUpdatePackage.CheckPath(target);
        if (!Directory.Exists(target)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(target, Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory)[Prefix.Length..], "N", out _)) continue;
            ApplicationUpdatePackage.CheckPath(directory);
            var journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath)) continue;
            yield return directory;
        }
    }

    private static void RecoverCore(string target)
    {
        foreach (var directory in Journals(target))
        {
            var journal = ReadJournal(directory);
            if (journal.State == "prepared") Rollback(directory, target, journal);
        }
    }

    internal static void RestoreAfterLaunchFailure(string transaction, string target)
    {
        using var targetLock = Lock(target);
        Rollback(transaction, target, ReadJournal(transaction));
    }

    private static FileStream Lock(string target)
    {
        var path = Path.Combine(Path.GetFullPath(target), ".boolka-update.lock");
        ApplicationUpdatePackage.CheckPath(path);
        // Одна блокировка на приложение, а не на отдельную загрузку обновления.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static ApplicationUpdateJournal ReadJournal(string directory)
    {
        var path = Path.Combine(directory, "journal.json");
        ApplicationUpdatePackage.CheckPath(path);
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Слишком большой журнал обновления.");
        var journal = JsonSerializer.Deserialize<ApplicationUpdateJournal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Не удалось прочитать журнал обновления.");
        if (journal.Files is null || journal.Originals is null || journal.Files.Any(file => file is null)
            || journal.Originals.Any(file => file is null)) throw new InvalidDataException("Повреждён состав журнала обновления.");
        ValidateNames(journal.Files.Select(file => file.Name));
        ValidateNames(journal.Originals.Select(file => file.Name));
        bool ValidHash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);
        if (journal.Files.Any(file => !ValidHash(file.Sha256))
            || journal.Originals.Any(file => file.Sha256 is not null && !ValidHash(file.Sha256))
            || !ValidHash(journal.Originals.Single(file => file.Name == "boolka.exe").Sha256))
            throw new InvalidDataException("Повреждены контрольные суммы журнала обновления.");
        if (journal.State == "prepared")
            foreach (var original in journal.Originals)
            {
                var backup = Path.Combine(directory, "backup", original.Name);
                ApplicationUpdatePackage.CheckPath(backup);
                if (File.Exists(backup) != (original.Sha256 is not null))
                    throw new InvalidDataException("Состав резервной копии отличается от журнала.");
            }
        if (journal.State is not ("prepared" or "completed" or "rolled-back")) throw new InvalidDataException("Неверное состояние обновления.");
        return journal;
    }

    private static void Rollback(string directory, string target, ApplicationUpdateJournal journal)
    {
        ApplicationUpdatePackage.CheckPath(directory);
        foreach (var original in journal.Originals.Reverse())
        {
            var destination = Path.Combine(target, original.Name);
            ApplicationUpdatePackage.CheckPath(destination);
            var current = File.Exists(destination) ? ApplicationUpdatePackage.Hash(destination) : null;
            var replacement = journal.Files.Single(file => file.Name == original.Name).Sha256;
            if (current == original.Sha256) continue;
            if (current is not null && current != replacement) throw new IOException("Откат не заменяет изменённые другим процессом файлы.");
            if (original.Sha256 is null)
            {
                if (File.Exists(destination)) File.Delete(destination);
                continue;
            }
            var backup = Path.Combine(directory, "backup", original.Name);
            if (ApplicationUpdatePackage.Hash(backup) != original.Sha256) throw new InvalidDataException("Повреждена резервная копия приложения.");
            var temporary = Path.Combine(directory, "restore", original.Name);
            Copy(backup, temporary, overwrite: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ApplicationUpdatePackage.CheckPath(destination);
            File.Move(temporary, destination, true);
        }
        Save(directory, journal with { State = "rolled-back" });
    }

    private static void ValidatePlan(ApplicationUpdatePlan plan, string workspace)
    {
        ValidateNames(plan.Files.Select(file => file.Name));
        ApplicationUpdatePackage.CheckPath(plan.Target);
        var expected = ApplicationReleaseClient.ReadVersion(plan.Version);
        foreach (var file in plan.Files)
            if (ApplicationUpdatePackage.Hash(Path.Combine(workspace, "payload", file.Name)) != file.Sha256)
                throw new InvalidDataException("Подготовленное обновление изменено.");
        ApplicationUpdatePackage.ValidateExecutable(Path.Combine(workspace, "payload", "boolka.exe"), expected);
    }

    private static void ValidateNames(IEnumerable<string> names)
    {
        var entries = names.ToArray();
        if (entries.Length != ApplicationUpdatePackage.FileNames.Length || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length
            || entries.Any(name => !ApplicationUpdatePackage.FileNames.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("Неверный состав плана обновления.");
    }

    private static void Copy(string source, string destination, bool overwrite = false)
    {
        ApplicationUpdatePackage.CheckPath(source);
        ApplicationUpdatePackage.CheckPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void Save(string directory, ApplicationUpdateJournal journal)
    {
        var path = Path.Combine(directory, "journal.json");
        ApplicationUpdatePackage.CheckPath(path);
        var temporary = path + ".tmp";
        ApplicationUpdatePackage.CheckPath(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
