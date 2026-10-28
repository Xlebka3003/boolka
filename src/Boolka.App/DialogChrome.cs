using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace Boolka;

/// <summary>Общий заголовок вспомогательных окон, независимый от системного цвета Windows.</summary>
internal static class DialogChrome
{
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
        "Applied", typeof(bool), typeof(DialogChrome), new PropertyMetadata(false));

    public static void Apply(Window window)
    {
        if ((bool)window.GetValue(AppliedProperty)) return;
        window.SetValue(AppliedProperty, true);
        window.WindowStyle = WindowStyle.None;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 36, CornerRadius = new CornerRadius(0), GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 6), UseAeroCaptionButtons = false
        });
        WindowTheme.Attach(window);
        WindowWorkArea.Attach(window);

        // Сначала отсоединяем содержимое, чтобы оно не получило второго логического родителя.
        var content = window.Content;
        window.Content = null;
        var frame = new DockPanel();
        frame.SetResourceReference(Panel.BackgroundProperty, "Theme.10131B");
        var header = new DockPanel { Height = 36, LastChildFill = true };
        header.SetResourceReference(Panel.BackgroundProperty, "Theme.151923");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            buttons.Children.Add(CaptionButton("minus", "Свернуть окно", () => SystemCommands.MinimizeWindow(window)));
            var maximize = CaptionButton("square", "Развернуть или восстановить окно", () =>
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            });
            window.StateChanged += (_, _) => ((LucideIcon)maximize.Content).Icon = window.WindowState == WindowState.Maximized ? "copy" : "square";
            buttons.Children.Add(maximize);
        }
        buttons.Children.Add(CaptionButton("x", "Закрыть", window.Close, true));
        var title = new TextBlock { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        frame.Children.Add(header);
        frame.Children.Add(new ContentPresenter { Content = content });
        window.Content = frame;
    }

    private static Button CaptionButton(string icon, string name, Action action, bool close = false)
    {
        var template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Background="{TemplateBinding Background}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
              </Border>
            </ControlTemplate>
            """);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Theme.E9EDF5")));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, new DynamicResourceExtension("KeyboardFocus")));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            close ? new SolidColorBrush(Color.FromRgb(196, 43, 28)) : new DynamicResourceExtension("Theme.353044")));
        if (close) hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Triggers.Add(hover);
        var button = new Button
        {
            Content = new LucideIcon { Icon = icon, Width = 18, Height = 18 }, Width = 46, Height = 36,
            Padding = new Thickness(0), Style = style
        };
        AutomationProperties.SetName(button, UiText.Get(name));
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }
}
