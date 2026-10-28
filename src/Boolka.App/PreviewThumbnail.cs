using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Boolka;

public sealed class PreviewThumbnail : Border
{
    public static readonly DependencyProperty AddonIdProperty = DependencyProperty.Register(nameof(AddonId), typeof(long?), typeof(PreviewThumbnail), new PropertyMetadata(null, IdChanged));
    public long? AddonId { get => (long?)GetValue(AddonIdProperty); set => SetValue(AddonIdProperty, value); }
    public static readonly DependencyProperty CategoryIdProperty = DependencyProperty.Register(nameof(CategoryId), typeof(long), typeof(PreviewThumbnail), new PropertyMetadata(0L, IdChanged));
    public long CategoryId { get => (long)GetValue(CategoryIdProperty); set => SetValue(CategoryIdProperty, value); }
    public static readonly DependencyProperty UseIconsProperty = DependencyProperty.Register(nameof(UseIcons), typeof(bool), typeof(PreviewThumbnail), new PropertyMetadata(false, IdChanged));
    public bool UseIcons { get => (bool)GetValue(UseIconsProperty); set => SetValue(UseIconsProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock placeholder = new() { Text = "ESO", Foreground = new SolidColorBrush(Color.FromRgb(161, 149, 197)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? request;

    public PreviewThumbnail()
    {
        SetResourceReference(CornerRadiusProperty, "Theme.Radius8"); SetResourceReference(BackgroundProperty, "Theme.2B273D"); ClipToBounds = true;
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var content = new Grid(); content.Children.Add(placeholder); content.Children.Add(image); Child = content;
        SetResourceReference(UseIconsProperty, "UseCategoryIcons");
        Loaded += (_, _) => LoadPreview();
        Unloaded += (_, _) => { request?.Cancel(); request?.Dispose(); request = null; };
    }

    private static void IdChanged(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((PreviewThumbnail)target).LoadPreview();

    private async void LoadPreview()
    {
        request?.Cancel(); request?.Dispose(); request = null;
        // Локальная игровая картинка появляется сразу, в том числе пока загружается превью.
        image.Source = CategoryIcons.Get(CategoryId); placeholder.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, UiText.Get("Иконка категории ESO"));
        if (UseIcons || !IsLoaded || AddonId is not > 0) return;
        var id = AddonId.Value;
        var current = new CancellationTokenSource(); request = current;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, UiText.Get("Загружаем превью…"));
        try
        {
            var result = await PreviewService.GetAsync(id, current.Token);
            if (current.IsCancellationRequested || request != current || !IsLoaded || AddonId != id || UseIcons) return;
            if (result is not null) image.Source = result;
            System.Windows.Automation.AutomationProperties.SetHelpText(this, result is null ? UiText.Get("У аддона нет доступного превью") : UiText.Get("Превью с ESOUI"));
        }
        catch (OperationCanceledException) { }
    }
}

