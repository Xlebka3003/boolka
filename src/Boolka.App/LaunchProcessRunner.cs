using System.Diagnostics;
using System.IO;

namespace Boolka;

internal sealed class LaunchProcessRunner : ILaunchRunner
{
    internal static ProcessStartInfo CreateStartInfo(string file)
    {
        var path = LaunchProgram.ValidatePath(file);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var info = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (extension is ".bat" or ".cmd")
        {
            info.FileName = Path.Combine(system, "cmd.exe");
            // Один проход подстановки: символы %, & и пробелы имени не превращаются в отдельную команду.
            info.Environment["BOOLKA_LAUNCH_FILE"] = path;
            info.Arguments = "/d /v:off /s /c \"\"%BOOLKA_LAUNCH_FILE%\"\"";
        }
        else if (extension == ".ps1")
        {
            info.FileName = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(path);
        }
        else info.FileName = path;
        return info;
    }

    public async Task RunAsync(LaunchProgram program, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (program.Kind == "harvest") EsoLaunchService.EnsureGameClosed();
        // Уже работающий exe не запускаем второй раз. Для ожидаемого шага ждём существующий процесс.
        var existing = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? EsoLaunchService.FindRunning(path) : null;
        using var process = existing ?? Process.Start(CreateStartInfo(path)) ?? throw new IOException(UiText.Get("Не удалось запустить программу."));
        if (!program.WaitForExit) return;
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new IOException(UiText.Get("Код завершения: ") + process.ExitCode);
    }

    public void StartGame(AppPreferences preferences) => EsoLaunchService.StartGame(preferences);
}
