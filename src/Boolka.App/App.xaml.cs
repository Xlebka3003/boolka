using System.Windows;
namespace Boolka;
public partial class App : Application
{
    public App()
    {
        // Подписываемся до загрузки XAML, чтобы сохранить также ошибки запуска.
        AppLog.Initialize();
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Fatal("crash.dispatcher", e.Exception);
            // Не объявляем исключение обработанным: продолжать работу после неизвестного сбоя небезопасно.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) AppLog.Fatal("crash.process", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("task.unobserved", e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"app.exit code={e.ApplicationExitCode}");
        AppLog.Close();
        base.OnExit(e);
    }
}
