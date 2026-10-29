using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace Boolka;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        // Помощник не создаёт Application и не запускает сканирование или автообновление модов.
        if (arguments.Length == 2 && arguments[0] is "--apply-app-update" or "--recover-app-update")
            return ApplyUpdate(arguments[1], arguments[0] == "--recover-app-update");
        if (arguments.Length != 0) return 2;
        try
        {
            if (ApplicationUpdateTransaction.NeedsRecovery(AppContext.BaseDirectory)) return StartRecovery();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            ShowUpdateError(error, AppContext.BaseDirectory);
            return 1;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int StartRecovery()
    {
        RequireUnelevatedUpdate();
        // Загруженный EXE Windows не даст заменить: восстановление выполняет его временная копия.
        var workspace = ApplicationUpdatePackage.CreateWorkspace();
        var executable = Environment.ProcessPath ?? throw new IOException("Не найден исполняемый файл приложения.");
        using var parent = Process.GetCurrentProcess();
        var info = FileVersionInfo.GetVersionInfo(executable);
        var plan = new ApplicationUpdatePlan(new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart).ToString(),
            Path.GetDirectoryName(executable)!, Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks,
            ApplicationUpdatePackage.Hash(executable), []);
        var planFile = Path.Combine(workspace, "plan.json");
        ApplicationUpdatePackage.WriteNewText(planFile, JsonSerializer.Serialize(plan));
        var helper = Path.Combine(workspace, "helper.exe");
        File.Copy(executable, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = workspace, CreateNoWindow = true };
        start.ArgumentList.Add("--recover-app-update");
        start.ArgumentList.Add(planFile);
        using var worker = Process.Start(start) ?? throw new IOException("Не удалось запустить восстановление приложения.");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(workspace, "ready")))
        {
            if (worker.HasExited || watch.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Помощник восстановления не готов.");
            Thread.Sleep(50);
        }
        ApplicationUpdatePackage.WriteNewText(Path.Combine(workspace, "approved"), "approved");
        return 0;
    }

    private static int ApplyUpdate(string planFile, bool recovery)
    {
        string? workspace = null;
        var parentExited = false;
        try
        {
            RequireUnelevatedUpdate();
            workspace = ApplicationUpdatePackage.ValidateWorkspace(Path.GetDirectoryName(Path.GetFullPath(planFile))!);
            var plan = ApplicationUpdatePackage.ReadPlan(planFile);
            if (!string.Equals(Environment.ProcessPath, Path.Combine(workspace, "helper.exe"), StringComparison.OrdinalIgnoreCase)
                || ApplicationUpdatePackage.Hash(Environment.ProcessPath!) != plan.PreviousExecutableHash)
                throw new InvalidDataException("Недопустимый помощник обновления.");
            ApplicationUpdatePackage.CheckPath(Path.Combine(workspace, "running.lock"));
            using var updateLock = new FileStream(Path.Combine(workspace, "running.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ApplicationUpdatePackage.WriteNewText(Path.Combine(workspace, "ready"), "ready");
            WaitForParent(plan);
            parentExited = true;
            ApplicationUpdatePackage.CheckPath(Path.Combine(workspace, "approved"));
            if (!File.Exists(Path.Combine(workspace, "approved"))) return 0;
            string? transaction = null;
            if (recovery) ApplicationUpdateTransaction.Recover(plan.Target);
            else transaction = ApplicationUpdateTransaction.Apply(plan, workspace);
            try
            {
                using var restarted = Process.Start(new ProcessStartInfo(Path.Combine(plan.Target, "boolka.exe"))
                { UseShellExecute = false, WorkingDirectory = plan.Target }) ?? throw new IOException("Не удалось перезапустить boolka.");
            }
            catch (Exception)
            {
                if (transaction is not null) ApplicationUpdateTransaction.RestoreAfterLaunchFailure(transaction, plan.Target);
                throw;
            }
            if (!recovery) UpdateInstalledVersion(plan.Target, plan.Version);
            ApplicationUpdatePackage.CheckPath(Path.Combine(workspace, "result.txt"));
            ApplicationUpdatePackage.WriteNewText(Path.Combine(workspace, "result.txt"), "success");
            return 0;
        }
        catch (Exception error)
        {
            if (workspace is not null)
            {
                try
                {
                    ApplicationUpdatePackage.CheckPath(Path.Combine(workspace, "result.txt"));
                    ApplicationUpdatePackage.WriteNewText(Path.Combine(workspace, "result.txt"), error.ToString());
                }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { Debug.WriteLine(logError); }
            }
            // Тихая проверка версии не показывает окон; ошибка уже начатой замены требует объяснения.
            if (parentExited) ShowUpdateError(error, workspace ?? "");
            return 1;
        }
    }

    private static void WaitForParent(ApplicationUpdatePlan plan)
    {
        if (plan.ParentId <= 0 || plan.ParentId == Environment.ProcessId || plan.ParentStart <= 0)
            throw new InvalidDataException("Неверный процесс приложения.");
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            try
            {
                if (parent.HasExited) return;
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStart
                    || !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.Target, "boolka.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Процесс приложения изменился.");
                if (!parent.WaitForExit(120000)) throw new IOException("Приложение не закрылось. Файлы не заменены.");
            }
            catch (InvalidOperationException) when (parent.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (parent.HasExited) { }
        }
    }

    private static void UpdateInstalledVersion(string target, string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{DD5CE4EC-EB35-4FC2-A8AB-A4C8CBEC805A}_is1", true);
            if (key?.GetValue("InstallLocation") is string location
                && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                key.SetValue("DisplayVersion", version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Debug.WriteLine(error); }
    }

    private static void ShowUpdateError(Exception error, string location)
    {
        UiText.Language = CultureInfo.CurrentUICulture.Name;
        var message = UiText.Get("Не удалось завершить обновление boolka. Файлы восстановления не удалены.") + "\n\n" + error.Message + "\n\n" + location;
        MessageBox.Show(message, "boolka", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal static void RequireUnelevatedUpdate()
    {
        // Данные подготовки лежат в пользовательской папке. Они не должны становиться
        // способом передачи кода процессу с повышенными правами через помощник.
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Для обновления перезапустите boolka без прав администратора или используйте установщик.");
    }
}
