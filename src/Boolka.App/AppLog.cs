using System.IO;

namespace Boolka;

internal static class AppLog
{
    private static DiagnosticLog? log;
    internal static bool PreviousCrash => log?.PreviousCrash == true;

    internal static void Initialize(string? directory = null)
    {
        Try(() =>
        {
            var previous = log;
            log = null;
            previous?.Dispose();
            log = new DiagnosticLog(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "boolka", "logs"));
        });
    }

    internal static void Info(string eventName) => Try(() => log?.Write("info", eventName));
    internal static void Error(string eventName, Exception error) => Try(() => log?.Write("error", eventName, error));
    internal static void Fatal(string eventName, Exception error) => Try(() =>
    {
        log?.Write("fatal", eventName, error);
        log?.MarkCrash();
    });
    internal static void AcknowledgeCrash() => Try(() => log?.AcknowledgeCrash());
    internal static void Close() => Try(() => { log?.Dispose(); log = null; });
    internal static Task ExportAsync(string destination) => Task.Run(() =>
    {
        var current = log;
        if (current is null) throw new IOException("Журнал недоступен. Проверьте доступ к папке приложения.");
        current.Export(destination);
    });

    private static void Try(Action action)
    {
        // Диагностика — вспомогательная подсистема: её ошибка не должна подменить исходный сбой или уронить приложение.
        try { action(); }
        catch (Exception) { }
    }
}
