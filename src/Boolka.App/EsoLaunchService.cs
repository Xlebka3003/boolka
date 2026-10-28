using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Boolka;

/// <summary>Запускает только выбранную игру и известные помощники из папки аддонов.</summary>
internal static class EsoLaunchService
{
    internal static bool HarvestNeedsPreparation(string root, string previous)
    {
        var current = HarvestStamp(root);
        return current.Length == 0 || current != previous;
    }

    internal static string HarvestStamp(string root)
    {
        var script = HarvestScript(root);
        var databases = DatabaseFiles(root).Where(file => file.Name.StartsWith("HarvestMap", StringComparison.Ordinal)).ToArray();
        if (script is null || databases.Length < 5) return "";
        var entries = new List<string>();
        foreach (var file in new[] { script, SafeFile(root, "HarvestMapData", "HarvestMapData.txt"), SafeFile(root, "HarvestMap", "HarvestMap.txt") })
            if (file is not null)
            {
                var info = new FileInfo(file);
                entries.Add(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            }
        entries.AddRange(databases.Select(file => file.Name + "|" + file.Modified.ToUniversalTime().Ticks));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    internal static string? HarvestScript(string folder) => new[] { "HarvestMapData", "HarvestMap" }
        .Select(name => SafeFile(folder, name, "DownloadNewData.bat")).FirstOrDefault(path => path is not null);

    internal static string? TtcClient(string folder) => SafeFile(folder, "TamrielTradeCentre", "Client", "Client.exe");

    internal static string? SafeFile(string root, params string[] parts)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(parts).ToArray()));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path)) return null;
        // Не запускаем файл, перенаправленный ссылкой за пределы выбранной папки.
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return null;
            if (string.Equals(item, fullRoot, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    internal static IReadOnlyList<(string Name, DateTime Modified)> DatabaseFiles(string root)
    {
        var result = new List<(string, DateTime)>();
        foreach (var region in new[] { "EU", "NA" })
            if (SafeFile(root, "TamrielTradeCentre", "PriceTable" + region + ".lua") is { } price)
                result.Add(("TTC " + region, File.GetLastWriteTime(price)));
        foreach (var name in new[] { "HarvestMapAD", "HarvestMapDC", "HarvestMapEP", "HarvestMapDLC", "HarvestMapNF" })
        {
            // Официальный HarvestMap-Data загружает базы внутрь Modules, а не в корень AddOns.
            var data = SafeFile(root, "HarvestMapData", "Modules", name, name + ".lua")
                ?? SafeFile(root, "HarvestMap", "Modules", name, name + ".lua")
                ?? SafeFile(root, name, name + ".lua");
            if (data is not null) result.Add((name, File.GetLastWriteTime(data)));
        }
        return result;
    }

    internal static Process? FindRunning(string path)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }

    internal static bool AlreadyRunning(string path)
    {
        using var process = FindRunning(path);
        return process is not null;
    }

    internal static void EnsureGameClosed()
    {
        foreach (var name in new[] { "eso", "eso64", "eso64-steam" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (!process.HasExited) throw new IOException(UiText.Get("Закройте ESO перед загрузкой базы HarvestMap."));
    }

    internal static void StartGame(AppPreferences preferences)
    {
        if (preferences.LaunchSteam)
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/306130") { UseShellExecute = true })?.Dispose();
            return;
        }
        var path = Path.GetFullPath(preferences.GameExecutable);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Select an existing ESO executable in launch settings.");
        if (!AlreadyRunning(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! })?.Dispose();
    }
}
