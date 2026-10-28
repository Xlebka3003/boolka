using System.IO;

namespace Boolka;

/// <summary>Единственное место, где старое имя используется для переноса существующих данных.</summary>
internal static class StorageMigration
{
    internal static string SettingsFolder(string localData)
    {
        var target = Path.Combine(localData, "boolka");
        MigrateDirectory(Path.Combine(localData, "Aster"), target, false);
        return target;
    }

    internal static string Transactions(string profileFolder)
    {
        var target = Path.Combine(profileFolder, ".boolka-installs");
        MigrateDirectory(Path.Combine(profileFolder, ".aster-installs"), target, true);
        return target;
    }

    private static void MigrateDirectory(string previous, string target, bool installationLock)
    {
        BackupFiles.Check(previous); BackupFiles.Check(target);
        if (!Directory.Exists(previous)) return;
        var parent = Path.GetDirectoryName(target)!;
        if (File.Exists(target)) throw new IOException("Путь boolka занят файлом: " + target);
        var migrationLock = Path.Combine(parent, ".boolka-migration.lock");
        BackupFiles.Check(migrationLock);
        using var migration = new FileStream(migrationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Directory.Exists(previous)) return;
        // Старый и новый установщик используют свои install.lock. Ни один не должен работать во время переноса.
        // FileShare.Delete позволяет перенести сам lock-файл, не разрешая чужую запись.
        using var oldLock = installationLock ? OpenInstallLock(previous) : null;
        // Windows запрещает переименовать родительский каталог, пока внутри открыт lock.
        // Для архива переносим элементы под двумя блокировками вместо переименования родителя.
        if (installationLock && !Directory.Exists(target)) Directory.CreateDirectory(target);
        using var newLock = installationLock && Directory.Exists(target) ? OpenInstallLock(target) : null;
        _ = BackupFiles.Walk(previous);
        if (!Directory.Exists(target))
        {
            BackupFiles.Check(previous); BackupFiles.Check(target);
            Directory.Move(previous, target);
            return;
        }
        _ = BackupFiles.Walk(target);
        // Если обе папки существуют, сохраняем обе версии конфликтующих файлов и журналов.
        // Перенос каждого элемента атомарен; после прерывания оставшиеся элементы можно перенести повторно.
        foreach (var source in Directory.EnumerateFileSystemEntries(previous))
        {
            BackupFiles.Check(source);
            var destination = Path.Combine(target, Path.GetFileName(source));
            if (File.Exists(destination) || Directory.Exists(destination))
                destination = Path.Combine(target, "migrated-" + Guid.NewGuid().ToString("N") + (File.Exists(source) ? "-" + Path.GetFileName(source) : ""));
            BackupFiles.Check(destination);
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
        BackupFiles.Check(previous);
        Directory.Delete(previous, false); // Только уже пустая служебная папка, без рекурсивного удаления.
    }

    private static FileStream OpenInstallLock(string folder)
    {
        var path = Path.Combine(folder, "install.lock");
        BackupFiles.Check(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
    }
}
