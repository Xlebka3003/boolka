using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Boolka;

public static class AddonMatcher
{
    public static bool IsTopLevelFolder(string folder) => !string.IsNullOrWhiteSpace(folder)
        && folder is not "." and not ".." && folder.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0
        && folder == folder.Trim();

    public static IReadOnlyList<AddonComparison> Compare(IReadOnlyList<Addon> installed, IReadOnlyList<Addon> catalog, bool catalogAvailable)
    {
        var index = catalog.SelectMany(addon => addon.CatalogFolders
                .Where(folder => IsTopLevelFolder(folder.Folder)).Select(folder => (folder.Folder, Addon: addon)))
            .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(x => x.Addon).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        return installed.Select(original =>
        {
            // Повторное сканирование не должно сохранять прежнее сопоставление.
            var local = original with { Comparison = null };
            AddonComparison Unknown(string reason, Addon? remote = null, string? build = null) => new(local, remote, UpdateStatus.Unknown, reason, build);
            if (!catalogAvailable) return new AddonComparison(local, null, UpdateStatus.NotChecked, "Нажмите «Пересканировать», чтобы получить каталог ESOUI.");
            var folder = Path.GetFileName(local.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (folder.Length == 0 || !index.TryGetValue(folder, out var candidates))
                return Unknown("В каталоге нет точного совпадения по папке аддона.");
            if (local.InstalledReleaseId is > 0 && !string.IsNullOrWhiteSpace(local.InstalledReleaseVersion))
            {
                var proven = candidates.Where(x => x.CatalogId == local.InstalledReleaseId).ToList();
                if (proven.Count != 1) return Unknown("Проверенный выпуск не найден однозначно среди владельцев этой папки.");
                var remoteRelease = proven[0];
                var releaseOrder = local.InstalledReleaseVersion.Equals(remoteRelease.Version, StringComparison.Ordinal)
                    ? 0 : CompareVersions(local.InstalledReleaseVersion, remoteRelease.Version);
                if (releaseOrder is null) return Unknown("Файлы проверенного выпуска подтверждены, но номера выпусков нельзя упорядочить.", remoteRelease);
                if (releaseOrder > 0) return Unknown("Установлен более новый проверенный выпуск. Понижение не предлагается.", remoteRelease);
                return new(local, remoteRelease, releaseOrder < 0 ? UpdateStatus.UpdateAvailable : UpdateStatus.VersionsMatch,
                    "Выпуск подтверждён квитанцией установки и SHA-256 всех файлов, независимо от устаревшей версии в манифесте.");
            }
            if (local.IsModuleContainer)
            {
                candidates = candidates.Where(candidate => ContainerMatches(local, candidate, folder)).ToList();
                if (candidates.Count != 1) return Unknown("Состав вложенных модулей не подтверждает единственный проект ESOUI.");
            }
            if (candidates.Count != 1)
            {
                // Старые патчи могут использовать папку оригинала. Различаем их только
                // по точному совпадению версии и числовой сборки, без угадывания по названию.
                var hasBuild = TryBuild(local.BuildVersion, out var localBuild);
                var localVersion = local.ManifestVersion ?? local.Version;
                var hasVersion = !string.IsNullOrWhiteSpace(local.ManifestVersion);
                var exact = candidates.Where(candidate => candidate.CatalogFolders.Count == 1 && (hasBuild
                    ? TryBuild(candidate.CatalogFolders[0].BuildVersion, out var candidateBuild) && localBuild == candidateBuild
                        && (!hasVersion || CompareVersions(localVersion, candidate.Version) == 0)
                    : hasVersion && CompareVersions(localVersion, candidate.Version) == 0)).ToList();
                // Версия без сборки допустима только при точном совпадении всего набора зависимостей.
                if (!hasBuild)
                    exact = exact.Where(candidate => MatchesActualModules(local, candidate, folder)).ToList();
                var alternativesDiffer = exact.Count == 1 && candidates.Where(x => x != exact[0]).All(candidate =>
                    hasVersion ? VersionsClearlyDiffer(localVersion, candidate.Version)
                    : candidate.CatalogFolders.Where(x => x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList() is var otherFolders
                        && otherFolders.Count == 1 && (TryBuild(otherFolders[0].BuildVersion, out var otherBuild) && otherBuild != localBuild
                            || string.IsNullOrWhiteSpace(otherFolders[0].BuildVersion) && MatchesActualModules(local, exact[0], folder)
                                && RootDependenciesDiffer(local, candidate, folder)));
                if (!alternativesDiffer)
                {
                    // Старый выпуск может отличаться от актуального номером, но автор,
                    // состав модулей и имена зависимостей должны подтверждать ровно один проект.
                    var compatible = candidates.Where(x => CompatibleIdentity(local, x, folder)).ToList();
                    if (compatible.Count != 1)
                        return Unknown($"Папку {folder} используют несколько проектов ESOUI ({candidates.Count}). Автоматический выбор ненадёжен.");
                    exact = compatible;
                }
                candidates = exact;
            }
            var remote = candidates[0];
            var folders = remote.CatalogFolders.Where(x => x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folders.Count != 1) return Unknown("Каталог содержит неоднозначные сведения об этой папке.", remote);
            var remoteBuild = folders[0].BuildVersion;
            int? buildOrder = TryBuild(local.BuildVersion, out var leftBuild) && TryBuild(remoteBuild, out var rightBuild)
                ? leftBuild.CompareTo(rightBuild) : null;
            // У пакета из нескольких папок общая версия может не совпадать с версией отдельного модуля.
            var versionOrder = remote.CatalogFolders.Count == 1 ? CompareVersions(local.ManifestVersion ?? local.Version, remote.Version) : null;
            if (buildOrder.HasValue && versionOrder.HasValue && versionOrder != 0 && (buildOrder == 0 || Math.Sign(buildOrder.Value) != Math.Sign(versionOrder.Value)))
                return Unknown("Номер сборки и строка версии противоречат друг другу. Проверьте страницу аддона.", remote, remoteBuild);
            var order = buildOrder ?? versionOrder;
            if (!order.HasValue) return Unknown("Недостаточно сопоставимых номеров сборок или версий. Сравнение по алфавиту не используется.", remote, remoteBuild);
            if (order > 0) return Unknown("Локальная версия новее версии из каталога. Понижение не предлагается.", remote, remoteBuild);
            var basis = buildOrder.HasValue ? "Сравнение по AddOnVersion для совпавшей папки." : "Сравнение по версии для единственной папки пакета.";
            return new AddonComparison(local, remote, order < 0 ? UpdateStatus.UpdateAvailable : UpdateStatus.VersionsMatch, basis, remoteBuild);
        }).ToList();
    }

    private static bool TryBuild(string value, out BigInteger number)
    {
        number = default;
        return value.Length is > 0 and <= 128 && value.All(char.IsAsciiDigit)
            && BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    private static bool ContainerMatches(Addon local, Addon candidate, string folder)
    {
        if (!local.LocalMetadataComplete || !candidate.DependencyMetadataValid) return false;
        var expected = candidate.PackageModules.Where(module => module.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (expected.Length == 0 || expected.Length != local.LocalModules.Count
            || candidate.PackageModules.Any(module => module.Path.Equals(folder, StringComparison.OrdinalIgnoreCase))) return false;
        return expected.All(module => local.LocalModules.Count(actual => actual.Path.Equals(module.Path, StringComparison.OrdinalIgnoreCase)
            && actual.RequiredDependencies.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(module.RequiredDependencies.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) == 1);
    }

    private static bool VersionsClearlyDiffer(string left, string right)
    {
        if (left.Length is 0 or > 128 || right.Length is 0 or > 128) return false;
        left = Regex.Replace(left.Trim(), @"^version\s*:?\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        right = Regex.Replace(right.Trim(), @"^version\s*:?\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (CompareVersions(left, right) is int order)
            return order != 0;

        // Суффикс релиза не задаёт порядок, но различает явно указанные версии старых патчей.
        const string label = @"^v?[0-9][0-9a-z ._()+-]{0,127}$";
        return Regex.IsMatch(left.Trim(), label, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(right.Trim(), label, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesActualModules(Addon local, Addon candidate, string folder)
    {
        if (!candidate.DependencyMetadataValid) return false;
        var roots = candidate.PackageModules.Where(x => x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roots.Count != 1 || !SameDependencies(roots[0].RequiredDependencies, local.RequiredDependencies)) return false;
        // Вложенные манифесты подтверждают состав установленного пакета, но не факт их загрузки игрой.
        foreach (var expected in candidate.PackageModules.Where(x => !x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            if (!local.LocalMetadataComplete || !DependencyResolver.IsSafePackagePath(expected.Path)
                || !expected.Path.Replace('\\', '/').StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return false;
            var actual = local.LocalModules.Where(x => x.Path.Replace('\\', '/').Equals(expected.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (actual.Count != 1 || !SameDependencies(expected.RequiredDependencies, actual[0].RequiredDependencies)) return false;
            if (expected.BuildVersion.Length > 0 && (!TryBuild(expected.BuildVersion, out var remoteBuild)
                || !TryBuild(actual[0].BuildVersion, out var actualBuild) || remoteBuild != actualBuild)) return false;
        }
        return true;
    }

    private static bool CompatibleIdentity(Addon local, Addon candidate, string folder)
    {
        if (!local.LocalMetadataComplete || !candidate.DependencyMetadataValid) return false;
        if (string.IsNullOrWhiteSpace(local.Author) || string.IsNullOrWhiteSpace(candidate.Author)
            || local.Author is "Автор не указан" or "Author not specified" or "Unknown"
            || candidate.Author is "Автор не указан" or "Author not specified" or "Unknown") return false;
        string[] Authors(string value) => Regex.Split(value, @"[,;&\r\n]+")
            .Select(x => x.Trim().TrimStart('@').Trim()).Where(x => x.Length > 0).ToArray();
        if (!Authors(local.Author).Intersect(Authors(candidate.Author), StringComparer.OrdinalIgnoreCase).Any()) return false;
        string[] Names(IReadOnlyList<string> dependencies) => dependencies.Select(x => x.Split(">=", 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = candidate.PackageModules.Where(x => x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (roots.Length != 1 || !Names(local.RequiredDependencies).SequenceEqual(Names(roots[0].RequiredDependencies), StringComparer.OrdinalIgnoreCase)) return false;
        if (!TryBuild(local.BuildVersion, out _) || !TryBuild(roots[0].BuildVersion, out _)) return false;
        foreach (var module in candidate.PackageModules.Where(x => !x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            if (!DependencyResolver.IsSafePackagePath(module.Path)
                || !module.Path.Replace('\\', '/').StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return false;
            var actual = local.LocalModules.Where(x => x.Path.Equals(module.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (actual.Length != 1 || !Names(actual[0].RequiredDependencies).SequenceEqual(Names(module.RequiredDependencies), StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SameDependencies(IReadOnlyList<string> left, IReadOnlyList<string> right) => left.Order(StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static bool RootDependenciesDiffer(Addon local, Addon candidate, string folder)
    {
        if (!candidate.DependencyMetadataValid) return false;
        var modules = candidate.PackageModules.Where(x => x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        return modules.Count == 1 && !SameDependencies(modules[0].RequiredDependencies, local.RequiredDependencies);
    }

    public static int? CompareVersions(string local, string remote)
    {
        local = local.Trim(); remote = remote.Trim();
        if (local.Length is 0 or > 128 || remote.Length is 0 or > 128) return null;
        // Встречается U51.0.1 в каталоге при 51.0.1 в манифесте. Подтверждаем
        // только равенство таких номеров, не предполагая порядок разных U-выпусков.
        var localUpdate = Regex.Replace(local, @"^[uU](?=\d+\.)", "");
        var remoteUpdate = Regex.Replace(remote, @"^[uU](?=\d+\.)", "");
        if (localUpdate == remoteUpdate && Regex.IsMatch(localUpdate, @"^\d+(?:\.\d+)+$")) return 0;
        // Equal explicit labels are comparable even when they include a suffix;
        // differing suffixes need version semantics the provider does not supply.
        if (local == remote && local.Any(char.IsAsciiDigit) && !local.StartsWith("Сборка ", StringComparison.Ordinal)) return 0;
        if (!Regex.IsMatch(local, @"^[vV]?[0-9]+(\.[0-9]+){0,15}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(remote, @"^[vV]?[0-9]+(\.[0-9]+){0,15}$", RegexOptions.CultureInvariant)) return null;
        var left = local.TrimStart('v', 'V').Split('.').Select(x => BigInteger.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var right = remote.TrimStart('v', 'V').Split('.').Select(x => BigInteger.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var order = (i < left.Length ? left[i] : BigInteger.Zero).CompareTo(i < right.Length ? right[i] : BigInteger.Zero);
            if (order != 0) return order;
        }
        return 0;
    }
}

