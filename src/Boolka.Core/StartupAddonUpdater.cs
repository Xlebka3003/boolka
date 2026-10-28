namespace Boolka;

internal enum StartupUpdateOutcome { Disabled, AlreadyAttempted, NotReady, GameRunning, NoUpdates, Completed }

/// <summary>Одна попытка после начальной проверки; ручное обновление не зависит от этого состояния.</summary>
internal sealed class StartupAddonUpdater
{
    private bool attempted;

    internal async Task<StartupUpdateOutcome> RunAsync(bool enabled, bool ready, bool hasUpdates,
        Func<bool> gameRunning, Func<Task> update, CancellationToken token)
    {
        if (!enabled) return StartupUpdateOutcome.Disabled;
        if (attempted) return StartupUpdateOutcome.AlreadyAttempted;
        attempted = true;
        token.ThrowIfCancellationRequested();
        if (!ready) return StartupUpdateOutcome.NotReady;
        if (!hasUpdates) return StartupUpdateOutcome.NoUpdates;
        if (gameRunning()) return StartupUpdateOutcome.GameRunning;
        await update();
        return StartupUpdateOutcome.Completed;
    }
}
