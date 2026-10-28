using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Boolka;

public sealed class SettingsWindow : Window
{
    private TextBlock? diagnosticInfo;
    private Button? diagnosticButton;

    internal void SetDiagnosticStatus(string text) { if (diagnosticInfo is not null) diagnosticInfo.Text = text; }
    internal void SetDiagnosticBusy(bool busy) { if (diagnosticButton is not null) diagnosticButton.IsEnabled = !busy; }

    public SettingsWindow(Window owner, AppPreferences preferences, Action<string, string, string> apply, Action openBackups, bool backupsAvailable, Action? saveDiagnostics = null, Action? openLaunchSettings = null)
    {
        if (owner.IsVisible) Owner = owner;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.10131B");
        Text(this, TitleProperty, "boolka · Настройки");
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 100) };
        var title = Label("Настройки", 25);
        panel.Children.Add(title);
        panel.Children.Add(Label("Язык", 14));
        var language = new ComboBox { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var option in UiLanguages.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Image
            {
                Width = 24, Height = 18, Margin = new Thickness(0, 0, 10, 0),
                Source = new BitmapImage(new Uri($"pack://application:,,,/boolka;component/Assets/Flags/{option.Flag}.png"))
            });
            row.Children.Add(new TextBlock { Text = option.NativeName, VerticalAlignment = VerticalAlignment.Center });
            language.Items.Add(new ComboBoxItem { Content = row, Tag = option.Code });
        }
        language.SelectedIndex = Array.FindIndex(UiLanguages.All, x => x.Code == UiLanguages.Normalize(preferences.Language));
        panel.Children.Add(language);
        var languageNotice = Label("Дополнительные языки в предварительной версии. Часть сообщений пока на английском.", 12);
        languageNotice.TextWrapping = TextWrapping.Wrap;
        languageNotice.Margin = new Thickness(0, 0, 0, 16);
        languageNotice.Visibility = preferences.Language is "de" or "fr" or "es" ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(languageNotice);
        panel.Children.Add(Label("Тема", 14));
        var theme = new ComboBox { Margin = new Thickness(0, 8, 0, 24), SelectedIndex = preferences.Theme switch { "graphite" or "dark" or "classic-dark" => 1, "warm" or "light" or "classic" => 2, _ => 0 } };
        foreach (var label in new[] { "Дракула", "Тёмная", "Светлая" })
        {
            var item = new ComboBoxItem();
            Text(item, ContentControl.ContentProperty, label);
            theme.Items.Add(item);
        }
        panel.Children.Add(theme);
        panel.Children.Add(Label("Изображения модов", 14));
        var previews = new ComboBox { Margin = new Thickness(0, 8, 0, 24), SelectedIndex = preferences.PreviewMode == "icons" ? 0 : 1 };
        foreach (var label in new[] { "Иконки", "Изображения" })
        {
            var item = new ComboBoxItem(); Text(item, ContentControl.ContentProperty, label); previews.Items.Add(item);
        }
        panel.Children.Add(previews);
        var backups = new Button { IsEnabled = backupsAvailable, HorizontalAlignment = HorizontalAlignment.Left };
        Text(backups, ContentControl.ContentProperty, "Резервные копии");
        backups.Click += (_, _) => openBackups();
        var tools = new WrapPanel();
        backups.Margin = new Thickness(0, 0, 8, 8);
        tools.Children.Add(backups);
        var diagnostics = new Button { IsEnabled = saveDiagnostics is not null, Margin = new Thickness(0, 0, 0, 8) };
        diagnosticButton = diagnostics;
        Text(diagnostics, ContentControl.ContentProperty, "Сохранить журнал");
        diagnostics.Click += (_, _) => saveDiagnostics?.Invoke();
        tools.Children.Add(diagnostics);
        var launch = new Button { IsEnabled = openLaunchSettings is not null, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        Text(launch, ContentControl.ContentProperty, "Запуск ESO и базы модов");
        launch.Click += (_, _) => openLaunchSettings?.Invoke();
        panel.Children.Add(launch);
        panel.Children.Add(tools);
        var explanation = Label("Создавайте полные копии модов и настроек или восстанавливайте их из сохранённого состояния.", 13);
        explanation.TextWrapping = TextWrapping.Wrap;
        explanation.Margin = new Thickness(0, 12, 0, 24);
        panel.Children.Add(explanation);
        diagnosticInfo = Label("При ошибке сохраните журнал и отправьте его разработчику. Автоматической отправки нет.", 13);
        diagnosticInfo.TextWrapping = TextWrapping.Wrap;
        diagnosticInfo.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(diagnosticInfo);
        var close = new Button { IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        Text(close, ContentControl.ContentProperty, "Закрыть");
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        // Выбор применяется сразу, а не откладывается до следующего запуска.
        string SelectedTheme() => theme.SelectedIndex switch { 1 => "graphite", 2 => "warm", _ => "dracula" };
        void ApplySelection()
        {
            var code = (language.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
            apply(code, SelectedTheme(), previews.SelectedIndex == 0 ? "icons" : "images");
            languageNotice.Visibility = code is "de" or "fr" or "es" ? Visibility.Visible : Visibility.Collapsed;
        }
        language.SelectionChanged += (_, _) => ApplySelection();
        theme.SelectionChanged += (_, _) => ApplySelection();
        previews.SelectionChanged += (_, _) => ApplySelection();
        DialogChrome.Apply(this);
    }

    private static TextBlock Label(string text, double size)
    {
        var label = new TextBlock { FontSize = size, Margin = new Thickness(0, 8, 0, 0) };
        Text(label, TextBlock.TextProperty, text);
        return label;
    }

    private static void Text(FrameworkElement element, DependencyProperty property, string text)
    {
        UiResources.Register(text);
        element.SetResourceReference(property, UiText.ResourceKey(text));
    }
}
