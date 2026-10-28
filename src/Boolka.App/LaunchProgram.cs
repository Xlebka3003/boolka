using System.IO;

namespace Boolka;

public sealed record LaunchProgram(string Name = "", string Path = "", bool Enabled = true,
    bool WaitForExit = false, bool Required = false, string Kind = "custom")
{
    internal static LaunchProgram[] FromPreferences(AppPreferences preferences) => preferences.LaunchPrograms ??
    [
        new("HarvestMap-Data", Enabled: preferences.LaunchHarvest, WaitForExit: true, Kind: "harvest"),
        new("Tamriel Trade Centre", Enabled: preferences.LaunchTtc, Kind: "ttc")
    ];

    internal static LaunchProgram[] Normalize(IEnumerable<LaunchProgram?> programs) => programs
        .Where(program => program is not null).Take(16).Select(program => program! with
        {
            Name = program.Kind == "harvest" && program.Name is "HarvestMap" or "" or null ? "HarvestMap-Data" : Trim(program.Name, 120),
            Path = Trim(program.Path, 2048),
            Kind = program.Kind is "harvest" or "ttc" ? program.Kind : "custom"
        }).ToArray();

    private static string Trim(string? value, int maximum)
    {
        var text = (value ?? "").Trim();
        return text[..Math.Min(text.Length, maximum)];
    }

    internal string? Resolve(string? folder) => !string.IsNullOrWhiteSpace(Path) ? Path
        : folder is null ? null : Kind switch
        {
            "harvest" => EsoLaunchService.HarvestScript(folder),
            "ttc" => EsoLaunchService.TtcClient(folder),
            _ => null
        };

    internal static bool Supported(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1";

    internal static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || !Supported(path) || !File.Exists(path))
            throw new IOException(UiText.Get("Файл не найден. Выберите новый путь в настройках запуска."));
        var full = System.IO.Path.GetFullPath(path);
        for (var item = full; item is not null; item = System.IO.Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.Get("Выберите файл без перенаправления через ссылку."));
        return full;
    }
}
