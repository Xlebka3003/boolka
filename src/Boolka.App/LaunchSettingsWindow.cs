using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Boolka;

internal sealed class LaunchSettingsWindow : Window
{
    private readonly AppPreferences initial;
    private readonly string? folder;
    private readonly List<LaunchProgram> programs;
    private readonly StackPanel programRows = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox gamePath;
    private readonly CheckBox steam;
    private readonly Button add;
    internal AppPreferences Selection { get; private set; }

    internal LaunchSettingsWindow(Window owner, AppPreferences preferences, string? folder)
    {
        initial = Selection = preferences;
        this.folder = folder;
        programs = LaunchProgram.FromPreferences(preferences).ToList();
        if (owner.IsVisible) Owner = owner;
        Title = UiText.Get("Запуск ESO и базы модов");
        Width = 800;
        Height = Math.Min(740, SystemParameters.WorkArea.Height - 60);
        MinWidth = 650;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.10131B");
        var frame = new DockPanel { Margin = new Thickness(22) };
        Content = frame;
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        frame.Children.Add(footer);
        var save = ActionButton("Сохранить", Save);
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        error.SetResourceReference(ForegroundProperty, "Theme.F1BDA9");
        error.Margin = new Thickness(0, 0, 12, 0);
        footer.Children.Add(error);
        var body = new StackPanel();
        frame.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var game = new StackPanel();
        game.Children.Add(Label("ESO", 20));
        steam = Option("Запускать через Steam", preferences.LaunchSteam, _ => { });
        game.Children.Add(steam);
        var gameFiles = new StackPanel();
        gamePath = PathText(preferences.GameExecutable);
        gameFiles.Children.Add(gamePath);
        gameFiles.Children.Add(ActionButton("Выбрать ESO.exe", (_, _) =>
        {
            var path = PickFile(true);
            if (path is not null) gamePath.Text = path;
        }));
        game.Children.Add(gameFiles);
        void GameMode() => gameFiles.Visibility = steam.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        steam.Checked += (_, _) => GameMode();
        steam.Unchecked += (_, _) => GameMode();
        GameMode();
        body.Children.Add(Card(game));
        var heading = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        add = ActionButton("Добавить программу", AddProgram);
        DockPanel.SetDock(add, Dock.Right);
        heading.Children.Add(add);
        heading.Children.Add(Label("Перед запуском игры", 18));
        body.Children.Add(heading);
        body.Children.Add(Label("Порядок — сверху вниз. Включите нужные программы.", 13));
        body.Children.Add(programRows);

        RefreshRows();
        DialogChrome.Apply(this);
    }

    private void RefreshRows()
    {
        error.Text = "";
        programRows.Children.Clear();
        add.IsEnabled = programs.Count < 16;
        for (var index = 0; index < programs.Count; index++)
        {
            var position = index;
            var program = programs[index];
            var panel = new StackPanel();
            var title = new DockPanel();
            var enabled = Option("", program.Enabled, value => programs[position] = programs[position] with { Enabled = value });
            DockPanel.SetDock(enabled, Dock.Left);
            title.Children.Add(enabled);
            if (program.Kind == "custom")
            {
                var name = PathText(program.Name);
                name.IsReadOnly = false;
                name.MaxLength = 120;
                name.TextChanged += (_, _) => programs[position] = programs[position] with { Name = name.Text };
                title.Children.Add(name);
            }
            else title.Children.Add(Label(program.Name, 16));
            panel.Children.Add(title);
            if (program.Kind == "ttc")
                panel.Children.Add(Label("Если ESO запускает Boolka, выберите в клиенте TTC: Setting → Auto Start Options → None.", 12));
            string? path = null;
            try { path = program.Resolve(folder); }
            catch (Exception exception) when (LaunchSequence.IsLaunchError(exception)) { error.Text = UiError.Describe(exception); }
            if (path is not null) panel.Children.Add(PathText(path));
            if (path is null || !File.Exists(path) || !LaunchProgram.Supported(path))
            {
                var missing = Label("Не найден — выберите файл.", 13);
                missing.SetResourceReference(ForegroundProperty, "Theme.F1BDA9");
                panel.Children.Add(missing);
            }
            if (program.Kind != "custom" && program.Path.Length == 0) panel.Children.Add(Label("Автопоиск в папке AddOns", 12));
            var actions = new WrapPanel();
            actions.Children.Add(ActionButton("Выбрать файл", (_, _) =>
            {
                var picked = PickFile(false);
                if (picked is not null) { programs[position] = programs[position] with { Path = picked }; RefreshRows(); }
            }));
            actions.Children.Add(ActionButton("Открыть папку", (_, _) => OpenProgramFolder(position)));
            if (program.Kind != "custom") actions.Children.Add(ActionButton("Автопоиск", (_, _) =>
            { programs[position] = programs[position] with { Path = "" }; RefreshRows(); }));
            var up = IconButton("arrow-up", "Выше", (_, _) => Move(position, -1));
            up.IsEnabled = position > 0;
            actions.Children.Add(up);
            var down = IconButton("arrow-down", "Ниже", (_, _) => Move(position, 1));
            down.IsEnabled = position + 1 < programs.Count;
            actions.Children.Add(down);
            if (program.Kind == "custom") actions.Children.Add(ActionButton("Удалить из списка", (_, _) =>
            { programs.RemoveAt(position); RefreshRows(); }));
            panel.Children.Add(actions);
            var advanced = new StackPanel();
            advanced.Children.Add(Option("Ждать завершения", program.WaitForExit, value =>
                programs[position] = programs[position] with { WaitForExit = value }));
            advanced.Children.Add(Option("Обязательная: при ошибке не запускать игру", program.Required, value =>
                programs[position] = programs[position] with { Required = value }));
            advanced.Children.Add(Label("У фоновых программ проверяется только запуск. Ошибки после запуска не отслеживаются.", 12));
            if (program.Kind == "harvest") advanced.Children.Add(Label("HarvestMap-Data: повторный запуск пропускается, пока файлы базы и аддона не изменились.", 12));
            panel.Children.Add(Advanced("Дополнительно", advanced));
            programRows.Children.Add(Card(panel));
        }
    }

    private void Move(int position, int delta)
    {
        var target = position + delta;
        if (target < 0 || target >= programs.Count) return;
        (programs[position], programs[target]) = (programs[target], programs[position]);
        RefreshRows();
    }

    private void AddProgram(object sender, RoutedEventArgs args)
    {
        if (programs.Count >= 16) return;
        var path = PickFile(false);
        if (path is null) return;
        programs.Add(new(Path.GetFileNameWithoutExtension(path), path, WaitForExit: !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)));
        RefreshRows();
    }

    private string? PickFile(bool game)
    {
        var picker = new OpenFileDialog
        {
            Filter = game ? "ESO (*.exe)|*.exe" : UiText.Get("Программы и скрипты") + "|*.exe;*.bat;*.cmd;*.ps1",
            CheckFileExists = true,
            Multiselect = false
        };
        if (folder is not null && Directory.Exists(folder)) picker.InitialDirectory = folder;
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private void OpenProgramFolder(int position)
    {
        try
        {
            var path = programs[position].Resolve(folder);
            var directory = path is null ? folder : Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is null || !Directory.Exists(directory)) throw new IOException(UiText.Get("Папка недоступна."));
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { directory } })?.Dispose();
        }
        catch (Exception exception) when (LaunchSequence.IsLaunchError(exception)) { error.Text = UiError.Describe(exception); }
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        try
        {
            if (steam.IsChecked != true)
            {
                if (!Path.GetExtension(gamePath.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException(UiText.Get("Выберите исполняемый файл ESO или Steam."));
                LaunchProgram.ValidatePath(gamePath.Text);
            }
            // Недоступный путь разрешаем сохранить: его можно исправить позднее, запуск покажет ошибку.
            Selection = initial with { LaunchConfigured = true, LaunchSteam = steam.IsChecked == true, GameExecutable = gamePath.Text, LaunchPrograms = LaunchProgram.Normalize(programs) };
            DialogResult = true;
        }
        catch (Exception exception) when (LaunchSequence.IsLaunchError(exception)) { error.Text = UiError.Describe(exception); }
    }

    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(14), Margin = new Thickness(0, 8, 12, 8), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Theme.191E29");
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.353C4E");
        return border;
    }

    private static TextBlock Label(string text, double size) => new()
    {
        Text = UiText.Get(text), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6)
    };

    private static Expander Advanced(string label, UIElement content)
    {
        var expander = new Expander { Header = UiText.Get(label), Content = content, Margin = new Thickness(0, 8, 0, 4) };
        expander.SetResourceReference(ForegroundProperty, "Theme.E9EDF5");
        return expander;
    }

    private static TextBox PathText(string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(5), Margin = new Thickness(0, 3, 0, 7) };
        box.SetResourceReference(BackgroundProperty, "Theme.10131B");
        box.SetResourceReference(ForegroundProperty, "Theme.E9EDF5");
        return box;
    }

    private static Button ActionButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = UiText.Get(text), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += action;
        return button;
    }

    private static Button IconButton(string icon, string label, RoutedEventHandler action)
    {
        var button = ActionButton(label, action);
        System.Windows.Automation.AutomationProperties.SetName(button, UiText.Get(label));
        button.Content = new LucideIcon { Icon = icon, Width = 16, Height = 16 };
        return button;
    }

    private static CheckBox Option(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = UiText.Get(label), IsChecked = value, Margin = new Thickness(0, 6, 8, 8), VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(ForegroundProperty, "Theme.E9EDF5");
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }
}
