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

    public SettingsWindow(Window owner, AppPreferences preferences, Action<string, string, string> apply, Action openBackups, bool backupsAvailable, Action? saveDiagnostics = null, Action? openLaunchSettings = null, Action<bool>? setStartupUpdates = null)
    {
        if (owner.IsVisible) Owner = owner;
        Width = 600;
        Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
        MinWidth = 520;
        MinHeight = Math.Min(360, Height);
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.10131B");
        Text(this, TitleProperty, "boolka · Настройки");
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var title = Label("Настройки", 25);
        title.TextAlignment = TextAlignment.Center;
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(title);
        var appearance = Section(panel, "Внешний вид");
        appearance.Children.Add(Label("Язык", 14));
        var language = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        // Шаблон создаёт отдельное представление выбранного языка и строки списка.
        // Общий UIElement в Content переиспользовался WPF и менял выравнивание после выбора.
        var languageTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Orientation="Horizontal" HorizontalAlignment="Left">
                <Image Width="24" Height="18" Margin="0,0,10,0" Source="{Binding FlagImage}" />
                <TextBlock Text="{Binding NativeName}" VerticalAlignment="Center" />
              </StackPanel>
            </DataTemplate>
            """);
        foreach (var option in UiLanguages.All)
        {
            language.Items.Add(new ComboBoxItem
            {
                Content = new
                {
                    option.NativeName,
                    FlagImage = new BitmapImage(new Uri($"pack://application:,,,/boolka;component/Assets/Flags/{option.Flag}.png"))
                },
                ContentTemplate = languageTemplate, Tag = option.Code
            });
        }
        language.SelectedIndex = Array.FindIndex(UiLanguages.All, x => x.Code == UiLanguages.Normalize(preferences.Language));
        appearance.Children.Add(language);
        appearance.Children.Add(Label("Тема", 14));
        var theme = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8), SelectedIndex = preferences.Theme switch { "graphite" or "dark" or "classic-dark" => 1, "warm" or "light" or "classic" => 2, _ => 0 } };
        foreach (var label in new[] { "Дракула", "Тёмная", "Светлая" })
        {
            var item = new ComboBoxItem();
            Text(item, ContentControl.ContentProperty, label);
            theme.Items.Add(item);
        }
        appearance.Children.Add(theme);
        appearance.Children.Add(Label("Изображения модов", 14));
        var previews = new ComboBox { HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8), SelectedIndex = preferences.PreviewMode == "icons" ? 0 : 1 };
        foreach (var label in new[] { "Иконки", "Изображения" })
        {
            var item = new ComboBoxItem(); Text(item, ContentControl.ContentProperty, label); previews.Items.Add(item);
        }
        appearance.Children.Add(previews);
        var startup = Section(panel, "Обновления и запуск");
        var startupUpdates = new CheckBox
        {
            Name = "StartupUpdatesToggle", IsChecked = preferences.AutoUpdateAddonsOnStartup,
            IsEnabled = setStartupUpdates is not null, Margin = new Thickness(0, 0, 0, 10)
        };
        Text(startupUpdates, ContentControl.ContentProperty, "Обновлять аддоны при запуске Boolka");
        startupUpdates.SetResourceReference(ForegroundProperty, "Theme.E9EDF5");
        startupUpdates.Checked += (_, _) => setStartupUpdates?.Invoke(true);
        startupUpdates.Unchecked += (_, _) => setStartupUpdates?.Invoke(false);
        startup.Children.Add(startupUpdates);
        var startupHint = Label("После проверки каталога. Если ESO запущена, автообновление пропускается.", 12);
        startupHint.TextWrapping = TextWrapping.Wrap;
        startupHint.Margin = new Thickness(0, 0, 0, 16);
        startup.Children.Add(startupHint);
        var data = Section(panel, "Данные и диагностика");
        var backups = new Button { IsEnabled = backupsAvailable, HorizontalAlignment = HorizontalAlignment.Left };
        Text(backups, ContentControl.ContentProperty, "Резервные копии");
        backups.Click += (_, _) => openBackups();

        backups.Margin = new Thickness(0, 0, 8, 8);
        data.Children.Add(backups);
        var diagnostics = new Button { HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = saveDiagnostics is not null, Margin = new Thickness(0, 0, 0, 8) };
        diagnosticButton = diagnostics;
        Text(diagnostics, ContentControl.ContentProperty, "Сохранить журнал");
        diagnostics.Click += (_, _) => saveDiagnostics?.Invoke();

        var launch = new Button { IsEnabled = openLaunchSettings is not null, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        Text(launch, ContentControl.ContentProperty, "Запуск ESO и базы модов");
        launch.Click += (_, _) => openLaunchSettings?.Invoke();
        startup.Children.Add(launch);

        var explanation = Label("Создавайте полные копии модов и настроек или восстанавливайте их из сохранённого состояния.", 13);
        explanation.TextWrapping = TextWrapping.Wrap;
        explanation.Margin = new Thickness(0, 2, 0, 16);
        data.Children.Add(explanation);
        data.Children.Add(diagnostics);
        diagnosticInfo = Label("При ошибке сохраните журнал и отправьте его разработчику. Автоматической отправки нет.", 13);
        diagnosticInfo.TextWrapping = TextWrapping.Wrap;
        diagnosticInfo.Margin = new Thickness(0, 2, 0, 0);
        data.Children.Add(diagnosticInfo);
        var developerPanel = new StackPanel();
        var developerCard = new Border
        {
            Child = developerPanel, Padding = new Thickness(16),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 20)
        };
        developerCard.SetResourceReference(Border.BackgroundProperty, "Theme.191E29");
        developerCard.SetResourceReference(Border.BorderBrushProperty, "Theme.A995EC");
        panel.Children.Add(developerCard);
        var developerTitle = Label("Разработчикам аддонов: зависимости", 14);
        developerTitle.FontWeight = FontWeights.SemiBold;
        developerTitle.Margin = new Thickness(0);
        developerTitle.SetResourceReference(TextBlock.ForegroundProperty, "Theme.A995EC");
        developerPanel.Children.Add(developerTitle);
        var developerSteps = new[]
        {
            "Укажите библиотеки в манифесте .txt или .addon.",
            "Используйте ## DependsOn: для библиотек на всех платформах, а ## PCDependsOn: — только для PC. В Windows Boolka учитывает оба поля вместе.",
            "Минимальную сборку укажите через >=, например LibAddonMenu-2.0>=43."
        };
        for (var index = 0; index < developerSteps.Length; index++)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new TextBlock { Text = (index + 1) + ".", FontWeight = FontWeights.SemiBold };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Theme.A995EC");
            row.Children.Add(number);
            var step = Label(developerSteps[index], 12);
            step.Margin = new Thickness(0);
            step.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(step, 1);
            row.Children.Add(step);
            developerPanel.Children.Add(row);
        }
        var developerNote = Label("Boolka проверяет манифест при сканировании и установке. Зависимости, указанные только в описании, автоматически не определяются.", 12);
        developerNote.Margin = new Thickness(0, 16, 0, 0);
        developerNote.TextWrapping = TextWrapping.Wrap;
        developerPanel.Children.Add(developerNote);
        var dependencyExample = new TextBox
        {
            Text = "## PCDependsOn: LibAddonMenu-2.0>=43 LibMapPins-1.0>=10047",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8)
        };
        dependencyExample.SetResourceReference(Control.BackgroundProperty, "Theme.191E29");
        dependencyExample.SetResourceReference(Control.ForegroundProperty, "Theme.E9EDF5");
        dependencyExample.SetResourceReference(Control.BorderBrushProperty, "Theme.353044");
        developerPanel.Children.Add(dependencyExample);
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
        }
        language.SelectionChanged += (_, _) => ApplySelection();
        theme.SelectionChanged += (_, _) => ApplySelection();
        previews.SelectionChanged += (_, _) => ApplySelection();
        DialogChrome.Apply(this);
    }

    private static StackPanel Section(Panel parent, string heading)
    {
        var body = new StackPanel();
        var card = new Border
        {
            Child = body, Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "Theme.191E29");
        card.SetResourceReference(Border.BorderBrushProperty, "Theme.353044");
        var title = Label(heading, 16);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(0, 0, 0, 12);
        title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.C8B7FF");
        body.Children.Add(title);
        parent.Children.Add(card);
        return body;
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
