using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Boolka;

public sealed class BackupsWindow : Window
{
    private readonly string folder;
    private readonly BackupService service = new();
    private readonly Action<bool> workingChanged;
    private readonly Func<Task> restored;
    private readonly ListBox list = new() { Margin = new Thickness(0, 16, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly Button restore;
    private readonly Button recover;
    private bool working;
    private bool pending;

    public BackupsWindow(Window owner, string folder, Action<bool> workingChanged, Func<Task> restored)
    {
        if (owner.IsVisible) Owner = owner;
        this.folder = folder;
        this.workingChanged = workingChanged;
        this.restored = restored;
        Title = UiText.Get("Резервные копии");
        Width = 820; Height = 580; MinWidth = 700; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.10131B");
        list.SetResourceReference(Control.BackgroundProperty, "Theme.191E29");
        list.SetResourceReference(Control.BorderBrushProperty, "Theme.353C4E");
        list.FontSize = 14;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.SetResourceReference(Panel.BackgroundProperty, "Theme.10131B");
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new() { Height = height });
        void Place(UIElement control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
        Place(new TextBlock { Text = Title, FontSize = 25, FontWeight = FontWeights.SemiBold }, 0);
        Place(new TextBlock
        {
            Text = UiText.Get("Полная копия AddOns и SavedVariables. Закройте ESO перед созданием и восстановлением.") + "\n" + folder,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 14
        }, 1);
        Place(list, 2);
        AddAction("Создать копию", async () =>
        {
            await service.CreateAsync(folder);
            return UiText.Get("Резервная копия создана.");
        });
        restore = AddAction("Восстановить", async () =>
        {
            if (list.SelectedItem is not BackupInfo { IsValid: true } selected) return "";
            var summary = UiText.Get("AddOns и SavedVariables будут целиком возвращены к выбранной дате. Моды и настройки, добавленные позже, исчезнут из текущих папок. Перед заменой будет создана отдельная копия текущего состояния. Закройте ESO.")
                + "\n\n" + selected + "\n\n" + folder;
            if (new OperationConfirmationWindow(this, UiText.Get("Восстановление копии"), summary).ShowDialog() != true) return "";
            await service.RestoreAsync(folder, selected.Id);
            await restored();
            return UiText.Get("Копия восстановлена. Прежнее состояние сохранено отдельно.");
        });
        AddAction("Обновить список", () => Task.FromResult(""));
        Place(actions, 3);
        var open = new Button { Content = UiText.Get("Открыть папку"), Padding = new Thickness(12, 8, 12, 8) };
        open.Click += (_, _) => OpenFolder(BackupService.StorageFolder(folder));
        actions.Children.Add(open);
        var archive = new Button { Content = UiText.Get("Архив обновлений"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0) };
        archive.Click += (_, _) =>
        {
            try { OpenFolder(StorageMigration.Transactions(Path.GetDirectoryName(folder)!)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Error("ui.operation.failed", ex);
                status.Text = UiText.Get("Не удалось открыть резервные копии: ") + ex.Message;
            }
        };
        actions.Children.Add(archive);
        recover = new Button { Content = UiText.Get("Вернуть прежнее состояние"), Visibility = Visibility.Collapsed, Padding = new Thickness(10, 7, 10, 7) };
        recover.Click += async (_, _) =>
        {
            if (new OperationConfirmationWindow(this, UiText.Get("Возврат после сбоя"), UiText.Get("Незавершённое восстановление будет отменено по журналу. Исходные папки вернутся на место, заменяющие файлы сохранятся отдельно. Закройте ESO.")).ShowDialog() == true)
                await Run(async () => { await service.RecoverAsync(folder); await restored(); return UiText.Get("Прежнее состояние возвращено."); });
        };
        recover.HorizontalAlignment = HorizontalAlignment.Left;
        recover.Margin = new Thickness(0, 8, 0, 0);
        Place(recover, 4);
        Place(status, 5);
        Content = grid;
        list.SelectionChanged += (_, _) =>
        {
            restore.IsEnabled = !working && !pending && list.SelectedItem is BackupInfo { IsValid: true };
            if (!working && list.SelectedItem is BackupInfo { Error: not null } damaged)
                status.Text = UiText.Get("Повреждённая копия") + ": " + UiText.Get(damaged.Error);
        };
        Loaded += async (_, _) => await Run(() => Task.FromResult(""));
        Closing += (_, e) => { if (working) e.Cancel = true; };
        restore.IsEnabled = false;
        DialogChrome.Apply(this);
    }

    private Button AddAction(string label, Func<Task<string>> operation)
    {
        var button = new Button { Content = UiText.Get(label), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) => await Run(operation);
        actions.Children.Add(button);
        return button;
    }

    private async Task Run(Func<Task<string>> operation)
    {
        if (working) return;
        working = true; workingChanged(true);
        actions.IsEnabled = false; list.IsEnabled = false; recover.IsEnabled = false;
        status.Text = UiText.Get("Работаем с резервной копией… Не закрывайте приложение.");
        try
        {
            var message = await operation();
            list.ItemsSource = await service.ListAsync(folder);
            pending = BackupService.HasPending(folder);
            recover.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            status.Text = pending ? UiText.Get("Есть незавершённая операция. Верните прежнее состояние перед продолжением.")
                : message.Length > 0 ? message : UiText.Get("Выберите копию для восстановления. Копии хранятся локально рядом с AddOns.");
            var invalid = list.Items.Cast<BackupInfo>().Count(x => !x.IsValid);
            if (invalid > 0) status.Text += "\n" + UiText.Get("Повреждённых копий: ") + invalid;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("ui.operation.failed", ex);
            status.Text = UiText.Get("Операция с копией не завершена: ") + UiText.Get(ex.Message);
            try { pending = BackupService.HasPending(folder); recover.Visibility = pending ? Visibility.Visible : Visibility.Collapsed; }
            catch (Exception check) when (check is IOException or UnauthorizedAccessException) { status.Text += "\n" + check.Message; }
        }
        finally
        {
            working = false; workingChanged(false);
            actions.IsEnabled = true; list.IsEnabled = true; recover.IsEnabled = true;
            restore.IsEnabled = !pending && list.SelectedItem is BackupInfo { IsValid: true };
        }
    }

    private void OpenFolder(string path)
    {
        if (working) return;
        try
        {
            BackupFiles.Check(path);
            if (!Directory.Exists(path)) { status.Text = UiText.Get("Папка появится после создания первой копии."); return; }
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("ui.operation.failed", ex);
            status.Text = UiText.Get("Не удалось открыть резервные копии: ") + ex.Message;
        }
    }
}
