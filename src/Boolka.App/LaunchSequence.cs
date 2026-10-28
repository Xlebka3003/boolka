using System.ComponentModel;
using System.IO;

namespace Boolka;

internal interface ILaunchRunner
{
    Task RunAsync(LaunchProgram program, string path, CancellationToken token);
    void StartGame(AppPreferences preferences);
}

internal sealed record LaunchReport(IReadOnlyList<string> Warnings, string HarvestStamp);

internal static class LaunchSequence
{
    internal static bool IsLaunchError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException;

    internal static async Task<LaunchReport> RunAsync(AppPreferences preferences, string? folder,
        ILaunchRunner runner, Action<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!preferences.LaunchSteam)
        {
            if (!Path.GetExtension(preferences.GameExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException(UiText.Get("Выберите исполняемый файл ESO или Steam."));
            LaunchProgram.ValidatePath(preferences.GameExecutable);
        }
        var warnings = new List<string>();
        var plan = new List<(LaunchProgram Program, string Path)>();
        // Отсутствующий обязательный файл обнаруживаем до запуска остальных помощников.
        foreach (var program in LaunchProgram.FromPreferences(preferences).Where(program => program.Enabled))
        {
            try { plan.Add((program, LaunchProgram.ValidatePath(program.Resolve(folder)))); }
            catch (Exception error) when (IsLaunchError(error))
            {
                AppLog.Error("launch.helper.failed", error);
                var message = program.Name + ": " + error.Message;
                if (program.Required) throw new IOException(message, error);
                warnings.Add(message);
            }
        }
        var stamp = preferences.HarvestPreparationStamp;
        foreach (var (program, path) in plan)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var automaticHarvest = program.Kind == "harvest" && program.Path.Length == 0 && folder is not null;
                if (automaticHarvest && !EsoLaunchService.HarvestNeedsPreparation(folder!, stamp)) continue;
                progress(program.Name);
                await runner.RunAsync(program, path, token);
                if (automaticHarvest && program.WaitForExit) stamp = EsoLaunchService.HarvestStamp(folder!);
            }
            catch (Exception error) when (IsLaunchError(error))
            {
                AppLog.Error("launch.helper.failed", error);
                var message = program.Name + ": " + error.Message;
                if (program.Required) throw new IOException(message, error);
                warnings.Add(message);
            }
        }
        token.ThrowIfCancellationRequested();
        runner.StartGame(preferences);
        return new(warnings, stamp);
    }
}
