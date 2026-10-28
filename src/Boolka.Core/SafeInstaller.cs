using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Boolka;

public sealed record InstallResult(string BackupDirectory, IReadOnlyList<string> InstalledFolders);

/// <summary>Stages every package before changing AddOns. Interrupted transactions require manual recovery.</summary>
public sealed class SafeInstaller
{
    public const long MaxExpandedBytes = 1024L * 1024 * 1024;
    public const long MaxFileBytes = 128L * 1024 * 1024;
    private readonly Action<string, string>? beforeMove;
    public SafeInstaller() { }
    internal SafeInstaller(Action<string, string> beforeMove) => this.beforeMove = beforeMove;
    private sealed record Journal(string Target, string State, IReadOnlyList<string> Folders, IReadOnlyList<string> PreviouslyPresent);
    private sealed record Entry(ZipArchiveEntry Source, string Relative, bool Directory);

    public Task<InstallResult> InstallAsync(DependencyPlan plan, IReadOnlyDictionary<long, string> archives,
        string addonFolder, CancellationToken cancellationToken, bool allowReplacement = true) => Task.Run(() => Install(plan, archives, addonFolder, cancellationToken, allowReplacement));

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.StartsWith('/') || path.StartsWith('\\')) return false;
        var pieces = path.Replace('\\', '/').TrimEnd('/').Split('/');
        if (pieces.Length > 24) return false;
        foreach (var piece in pieces)
        {
            if (piece.Length == 0 || piece is "." or ".." || piece.EndsWith('.') || piece.EndsWith(' ') || piece != piece.Trim()
                || piece.Any(c => c < 32 || "<>:\"|?*~".Contains(c)) || piece.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) return false;
            var stem = piece.Split('.')[0].TrimEnd(' ', '.');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')) return false;
        }
        return true;
    }

    private static bool IsExcelLockFile(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash <= 0) return false;
        var name = path[(slash + 1)..];
        if (!name.StartsWith("~$", StringComparison.Ordinal) || name.Length <= 2) return false;
        var extension = Path.GetExtension(name);
        if (!new[] { ".xls", ".xlsx", ".xlsm", ".xlsb" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        // Файл блокировки Excel не нужен аддону. Проверяем путь без служебного префикса,
        // чтобы исключение не разрешало обход каталогов, короткие имена Windows и другие символы ~.
        return IsSafeRelativePath(path[..(slash + 1)] + name[2..]);
    }

    private InstallResult Install(DependencyPlan plan, IReadOnlyDictionary<long, string> archives, string folder, CancellationToken token, bool allowReplacement)
    {
        if (!plan.IsReady || plan.Packages.Count is 0 or > 100) throw new InvalidDataException("Нет безопасного плана установки.");
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Нужен полный путь AddOns.");
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(folder) || !Path.GetFileName(folder).Equals("AddOns", StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите существующую папку с именем AddOns.");
        CheckAncestors(folder);
        var parent = Directory.GetParent(folder)?.FullName ?? throw new IOException("Нельзя устанавливать в корень диска.");
        var transactions = StorageMigration.Transactions(parent);
        if (File.Exists(transactions)) throw new IOException("Путь транзакций занят файлом.");
        Directory.CreateDirectory(transactions); CheckAncestors(transactions);
        var lockPath = Path.Combine(transactions, "install.lock");
        if (File.Exists(lockPath)) CheckFile(lockPath);
        using var operationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(transactions))
        {
            CheckAncestors(transactions);
            foreach (var directory in Directory.EnumerateDirectories(transactions))
            {
                CheckAncestors(directory);
                var pending = Path.Combine(directory, "pending.json");
                if (!File.Exists(pending)) continue;
                CheckFile(pending);
                if (new FileInfo(pending).Length > 128 * 1024) throw new IOException("Слишком большой журнал. Требуется ручная проверка: " + pending);
                Journal? existing;
                try { existing = JsonSerializer.Deserialize<Journal>(File.ReadAllText(pending)); }
                catch (JsonException) { throw new IOException($"Повреждён журнал установки: {pending}. Требуется ручная проверка."); }
                if (existing is null || string.IsNullOrWhiteSpace(existing.Target) || existing.Folders is null || existing.PreviouslyPresent is null
                    || existing.Target.Equals(folder, FileSystemPaths.Comparison))
                    throw new IOException($"Незавершённая установка. Проверьте резервные копии и журнал: {directory}. Автоматическое продолжение отключено.");
            }
        }
        token.ThrowIfCancellationRequested();
        var verifiedReleases = InstalledReleaseStore.LoadVerified(folder);
        foreach (var package in plan.Packages)
            foreach (var prior in verifiedReleases.Values.Where(x => x.Id == package.CatalogId).Distinct())
            {
                var order = AddonMatcher.CompareVersions(prior.Version, package.Version);
                if (order is > 0 || order is null && !prior.Version.Equals(package.Version, StringComparison.Ordinal))
                    throw new InvalidDataException($"Проверенный выпуск {package.Name} новее или не сопоставим с выбранным выпуском. Понижение запрещено.");
            }
        var roots = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in plan.Packages)
        {
            if (package.CatalogId is not > 0 || !archives.ContainsKey(package.CatalogId.Value) || !package.DependencyMetadataValid || package.PackageModules.Count == 0)
                throw new InvalidDataException("Не хватает архива или сведений о модулях пакета.");
            if (package.PackageModules.Select(x => x.Path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.PackageModules.Count)
                throw new InvalidDataException("Повторные пути модулей в пакете.");
            foreach (var module in package.PackageModules)
            {
                if (!IsSafeRelativePath(module.Path)) throw new InvalidDataException("Недопустимый путь модуля.");
                var root = module.Path.Replace('\\', '/').Split('/')[0];
                if (roots.TryGetValue(root, out var owner) && owner != package.CatalogId) throw new InvalidDataException($"Несколько пакетов заменяют папку {root}.");
                roots[root] = package.CatalogId.Value;
            }
        }
        var names = roots.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!allowReplacement && names.Any(name => Directory.Exists(Path.Combine(folder, name)) || File.Exists(Path.Combine(folder, name))))
            throw new InvalidDataException(UiText.Get("Импорт набора не заменяет существующие папки. Проверьте состав заново."));
        // Корень может быть контейнером вложенных модулей без собственного манифеста.
        // Ниже каждый объявленный модуль всё равно проверяется по точному пути, сборке и зависимостям.
        var previous = new List<string>();
        foreach (var name in names)
        {
            var target = Path.Combine(folder, name);
            if (File.Exists(target)) throw new IOException($"Путь аддона занят файлом: {target}");
            if (Directory.Exists(target)) { CheckTree(target); previous.Add(name); }
        }
        Directory.CreateDirectory(transactions); CheckAncestors(transactions);
        var operation = Path.Combine(transactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operation);
        var stage = Path.Combine(operation, "staging"); var backup = Path.Combine(operation, "backup");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(backup);
        var pendingPath = Path.Combine(operation, "pending.json");
        var movedOld = new List<string>(); var movedNew = new List<string>();
        var opened = new List<ZipArchive>(); var streams = new List<FileStream>();
        var extracted = new List<string>(); var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool started = false, completed = false;
        try
        {
            var entries = new List<Entry>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
            foreach (var package in plan.Packages)
            {
                token.ThrowIfCancellationRequested();
                var archivePath = Path.GetFullPath(archives[package.CatalogId!.Value]); CheckFile(archivePath);
                var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read); streams.Add(stream);
                if (stream.Length > ArchiveDownloader.MaxArchiveBytes) throw new InvalidDataException("Превышен размер ZIP.");
                ArchiveDownloader.ValidateDirectoryAsync(stream, token).GetAwaiter().GetResult(); stream.Position = 0;
                var zip = new ZipArchive(stream, ZipArchiveMode.Read, true); opened.Add(zip);
                long documentationBytes = 0;
                foreach (var entry in zip.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/'); var isDirectory = relative.EndsWith('/'); relative = relative.TrimEnd('/');
                    var skipLockFile = !isDirectory && IsExcelLockFile(relative);
                    // У разных ZIP могут быть одинаковые отбрасываемые README в корне.
                    // Внутри одного ZIP повторы запрещены; рабочие корни имеют единственного владельца.
                    if ((!IsSafeRelativePath(relative) && !skipLockFile) || !seen.Add(package.CatalogId + ":" + relative) || seen.Count > 50000) throw new InvalidDataException($"Недопустимый или повторный путь ZIP: {entry.FullName}");
                    var root = relative.Split('/')[0];
                    var declaredRoot = roots.TryGetValue(root, out var owner) && owner == package.CatalogId;
                    if (!declaredRoot && roots.ContainsKey(root) || declaredRoot && !isDirectory && relative.IndexOf('/') < 0)
                        throw new InvalidDataException($"Архив содержит необъявленную папку или файл: {entry.FullName}");
                    var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                    // Пустая запись каталога может нести исходный Windows reparse-атрибут.
                    // Её не извлекаем: обычные каталоги ниже создаются по проверенным путям файлов.
                    if (declaredRoot && isDirectory && entry.Length == 0 && mode is 0 or 0x4000
                        && (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) continue;
                    if (mode is not (0 or 0x4000 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Ссылки и специальные файлы в ZIP запрещены.");
                    if (entry.Length < 0 || entry.Length > MaxFileBytes || (expanded += entry.Length) > MaxExpandedBytes || isDirectory && entry.Length != 0)
                        throw new InvalidDataException("Превышены ограничения размера распакованного архива.");
                    // Необъявленный корень разрешён только как набор отбрасываемых документов.
                    // Любой манифест, код или другой файл в нём отменяет всю транзакцию.
                    if (!declaredRoot)
                    {
                        if (!isDirectory && (!ArchiveDocumentation.CanDiscard(entry, relative)
                            || (documentationBytes += entry.Length) > ArchiveDocumentation.MaxTotalBytes))
                            throw new InvalidDataException($"Архив содержит необъявленную папку или файл: {entry.FullName}");
                        continue;
                    }
                    // Пропускаем только после проверок владельца, типа, размера и уникальности записи.
                    if (skipLockFile) continue;
                    entries.Add(new(entry, relative, isDirectory));
                }
            }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(stage, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
                var directory = entry.Directory ? destination : Path.GetDirectoryName(destination)!;
                CreateTrackedDirectory(directory, stage, createdDirectories);
                if (entry.Directory) continue;
                using var source = entry.Source.Open(); using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                extracted.Add(destination); var buffer = new byte[81920]; long written = 0;
                while (true) { token.ThrowIfCancellationRequested(); var count = source.Read(buffer); if (count == 0) break; written += count; if (written > entry.Source.Length || written > MaxFileBytes) throw new InvalidDataException("Размер ZIP не совпал с каталогом."); target.Write(buffer, 0, count); }
                if (written != entry.Source.Length) throw new InvalidDataException("Неполный файл в ZIP.");
            }
            foreach (var package in plan.Packages)
                foreach (var module in package.PackageModules)
                {
                    var modulePath = module.Path.Replace('\\', '/'); var leaf = modulePath.Split('/')[^1];
                    var directory = Path.Combine(stage, modulePath.Replace('/', Path.DirectorySeparatorChar));
                    var manifest = new[] { Path.Combine(directory, leaf + ".txt"), Path.Combine(directory, leaf + ".addon") }.FirstOrDefault(File.Exists);
                    if (manifest is null || new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException($"В архиве нет корректного манифеста модуля {module.Path}.");
                    var fields = ReadManifest(manifest);
                    if (module.BuildVersion.Length > 0 && fields.GetValueOrDefault("AddOnVersion", "") != module.BuildVersion) throw new InvalidDataException($"Сборка модуля {module.Path} отличается от каталога.");
                    var declared = module.RequiredDependencies.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    var actual = fields.GetValueOrDefault("DependsOn", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    // Каталог может повторять зависимости вложенного модуля у корня пакета.
                    // Разрешаем избыточный план, но запрещаем любую незапланированную зависимость.
                    if (actual.Except(declared, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException($"Зависимости {module.Path} отличаются от проверенного плана.");
                    var oldManifest = new[] { Path.Combine(folder, modulePath.Replace('/', Path.DirectorySeparatorChar), leaf + ".txt"), Path.Combine(folder, modulePath.Replace('/', Path.DirectorySeparatorChar), leaf + ".addon") }.FirstOrDefault(File.Exists);
                    if (oldManifest is not null)
                    {
                        CheckFile(oldManifest);
                        if (new FileInfo(oldManifest).Length > 1024 * 1024) throw new InvalidDataException("Существующий манифест слишком велик для проверки версии.");
                        var old = ReadManifest(oldManifest); var oldBuild = old.GetValueOrDefault("AddOnVersion", ""); var newBuild = fields.GetValueOrDefault("AddOnVersion", "");
                        if (TryBuild(oldBuild, out var oldNumber) && (!TryBuild(newBuild, out var newNumber) || oldNumber > newNumber)
                            || AddonMatcher.CompareVersions(old.GetValueOrDefault("Version", ""), fields.GetValueOrDefault("Version", "")) is > 0)
                            throw new InvalidDataException($"Установка понизит или не подтверждает сборку {module.Path}. Операция отменена.");
                    }
                }
            VerifyRequirements(plan.Packages, folder, roots.Keys);
            token.ThrowIfCancellationRequested();
            InstalledReleaseStore.Write(operation, folder, plan.Packages, stage, token);
            // Journal reaches disk before the first live-folder move. Cancellation is deferred during commit/rollback.
            WriteJournal(pendingPath, new(folder, "committing", names, previous)); started = true;
            foreach (var name in names)
            {
                CheckAncestors(folder); CheckAncestors(stage); CheckAncestors(backup);
                var destination = Path.Combine(folder, name);
                if (previous.Contains(name)) { CheckTree(destination); Move(destination, Path.Combine(backup, name)); movedOld.Add(name); }
                else if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Папка назначения появилась во время установки.");
                Move(Path.Combine(stage, name), destination); movedNew.Add(name);
            }
            File.Move(pendingPath, Path.Combine(operation, "completed.json"));
            completed = true;
            return new(backup, names);
        }
        catch (Exception failure)
        {
            if (started)
            {
                try
                {
                    foreach (var name in movedNew.AsEnumerable().Reverse()) Move(Path.Combine(folder, name), Path.Combine(stage, name));
                    foreach (var name in movedOld.AsEnumerable().Reverse()) Move(Path.Combine(backup, name), Path.Combine(folder, name));
                    File.Move(pendingPath, Path.Combine(operation, "rolled-back.json"));
                }
                catch (Exception rollback) { throw new IOException($"Установка прервана, восстановление не завершено. Ничего не удаляйте. Журнал и резервные копии: {operation}", new AggregateException(failure, rollback)); }
            }
            throw;
        }
        finally
        {
            foreach (var zip in opened) zip.Dispose(); foreach (var stream in streams) stream.Dispose();
            // Only files created by this operation and empty staging directories are removed.
            foreach (var file in extracted) { try { if (File.Exists(file)) { CheckFile(file); File.Delete(file); } } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            foreach (var directory in createdDirectories.OrderByDescending(x => x.Length)) TryDeleteEmpty(directory);
            TryDeleteEmpty(stage); if (!completed) TryDeleteEmpty(backup); TryDeleteEmpty(operation);
        }
    }
    private void Move(string source, string destination) { beforeMove?.Invoke(source, destination); CheckAncestors(source); CheckAncestors(Path.GetDirectoryName(destination)!); Directory.Move(source, destination); }
    private static bool TryBuild(string value, out BigInteger number) { number = default; return value.Length is > 0 and <= 128 && value.All(char.IsAsciiDigit) && BigInteger.TryParse(value, out number) && number > 0; }
    private static void VerifyRequirements(IReadOnlyList<Addon> packages, string target, IEnumerable<string> replacedRoots)
    {
        var modules = packages.SelectMany(x => x.PackageModules).ToList(); var replaced = replacedRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var edges = 0;
        void Require(string requirement, int depth)
        {
            if (++edges > 2000 || depth > 64 || requirement.Length > 256) throw new InvalidDataException("Слишком большой список зависимостей при повторной проверке.");
            var match = Regex.Match(requirement, @"^([^\s<>=]+)(?:>=([0-9]{1,128}))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !AddonMatcher.IsTopLevelFolder(match.Groups[1].Value) || !IsSafeRelativePath(match.Groups[1].Value)) throw new InvalidDataException("Не поддерживается зависимость: " + requirement);
            var name = match.Groups[1].Value; var minimum = match.Groups[2].Value;
            bool Satisfies(string build) => minimum.Length == 0 || TryBuild(build, out var value) && value >= BigInteger.Parse(minimum);
            if (modules.Any(m => m.Path.Replace('\\', '/').Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase) && Satisfies(m.BuildVersion))) return;
            if (!replaced.Contains(name))
            {
                var directory = Path.Combine(target, name);
                var manifest = new[] { Path.Combine(directory, name + ".txt"), Path.Combine(directory, name + ".addon") }.FirstOrDefault(File.Exists);
                if (manifest is not null)
                {
                    CheckTree(directory); CheckFile(manifest);
                    if (new FileInfo(manifest).Length <= 1024 * 1024)
                    {
                        var fields = ReadManifest(manifest);
                        if (Satisfies(fields.GetValueOrDefault("AddOnVersion", "")))
                        {
                            if (visited.Add(name)) foreach (var child in fields.GetValueOrDefault("DependsOn", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) Require(child, depth + 1);
                            return;
                        }
                    }
                }
            }
            throw new InvalidDataException("Обязательная зависимость отсутствует или изменилась: " + requirement);
        }
        foreach (var requirement in modules.SelectMany(x => x.RequiredDependencies)) Require(requirement, 0);
    }
    private static Dictionary<string, string> ReadManifest(string path) => File.ReadLines(path).Where(x => x.StartsWith("##") && x.Contains(':')).Select(x => x[2..].Split(':', 2)).GroupBy(x => ManifestFields.Normalize(x[0]), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Key.Equals("DependsOn", StringComparison.OrdinalIgnoreCase) ? string.Join(" ", x.Select(y => y[1].Trim())) : x.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
    private static void WriteJournal(string path, Journal journal) { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(file, journal); file.Flush(true); }
    private static void CheckFile(string path) { CheckAncestors(Path.GetDirectoryName(path)!); if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки файлов запрещены: " + path); }
    private static void CheckAncestors(string path) { for (var info = new DirectoryInfo(path); info is not null; info = info.Parent) if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папки-ссылки запрещены: " + info.FullName); }
    private static void CheckTree(string root) { CheckAncestors(root); var pending = new Stack<string>(); pending.Push(root); var count = 0; while (pending.Count > 0) foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop())) { if (++count > 100000) throw new IOException("Слишком много файлов в существующем аддоне."); if (Path.GetFileName(entry).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) throw new IOException("Внутри аддона есть SavedVariables. Автоматическая замена запрещена."); var attributes = File.GetAttributes(entry); if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки внутри аддона запрещены."); if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); } }
    private static void CreateTrackedDirectory(string path, string root, HashSet<string> created) { var missing = new Stack<string>(); for (var current = path; !Directory.Exists(current); current = Path.GetDirectoryName(current)!) missing.Push(current); while (missing.Count > 0) { var current = missing.Pop(); CheckAncestors(Path.GetDirectoryName(current)!); Directory.CreateDirectory(current); created.Add(current); } CheckAncestors(path); }
    private static void TryDeleteEmpty(string path) { try { if (Directory.Exists(path)) { CheckAncestors(path); Directory.Delete(path, false); } } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
