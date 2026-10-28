using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Boolka;

/// <summary>Поиск меняет видимость строк, сохраняя выбранную категорию и её ID.</summary>
public static class CategorySearch
{
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached("Handler", typeof(EventHandler), typeof(CategorySearch));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CategorySearch), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(CategorySearch), new PropertyMetadata("", TextChanged));
    public static readonly DependencyProperty NoResultsProperty = DependencyProperty.RegisterAttached("NoResults", typeof(bool), typeof(CategorySearch), new PropertyMetadata(false));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static string GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty) ?? "";
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
    public static bool GetNoResults(DependencyObject obj) => (bool)obj.GetValue(NoResultsProperty);
    public static void SetNoResults(DependencyObject obj, bool value) => obj.SetValue(NoResultsProperty, value);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.GetValue(HandlerProperty) is EventHandler previous)
            combo.ItemContainerGenerator.StatusChanged -= previous;
        if ((bool)e.NewValue)
        {
            combo.DropDownOpened += Opened;
            combo.DropDownClosed += Closed;
            EventHandler handler = (_, _) =>
            {
                if (combo.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated) Apply(combo);
            };
            combo.SetValue(HandlerProperty, handler);
            combo.ItemContainerGenerator.StatusChanged += handler;
        }
        else
        {
            combo.DropDownOpened -= Opened;
            combo.DropDownClosed -= Closed;
            combo.ClearValue(HandlerProperty);
            SetText(combo, "");
        }

    }

    private static void TextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ComboBox combo) Apply(combo);
    }

    public static bool Matches(AddonCategory category, string query) => category.Id == 0 || string.IsNullOrWhiteSpace(query)
        || category.ToString().Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || category.OriginalName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Apply(ComboBox combo)
    {
        var query = GetText(combo);
        var found = 0;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            var matches = combo.Items[i] is AddonCategory category && Matches(category, query);
            if (matches && combo.Items[i] is AddonCategory { Id: not 0 }) found++;
            if (combo.ItemContainerGenerator.ContainerFromIndex(i) is ComboBoxItem item)
                item.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
        }
        SetNoResults(combo, query.Length > 0 && found == 0);
    }

    private static void Opened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        Apply(combo);
        combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!combo.IsDropDownOpen || combo.Template.FindName("PART_SearchBox", combo) is not TextBox search) return;
            search.PreviewKeyDown -= SearchKeyDown;
            search.PreviewKeyDown += SearchKeyDown;
            search.Tag = combo;
            search.Focus();
        }));
    }

    private static void Closed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo) SetText(combo, "");
    }

    private static void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: ComboBox combo }) return;
        if (e.Key == Key.Escape)
        {
            combo.IsDropDownOpen = false;
            combo.Focus();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            var first = combo.Items.OfType<AddonCategory>().FirstOrDefault(x => x.Id != 0 && Matches(x, GetText(combo)));
            if (first is null) return;
            if (e.Key == Key.Enter)
            {
                combo.SelectedItem = first;
                combo.IsDropDownOpen = false;
                combo.Focus();
            }
            else if (combo.ItemContainerGenerator.ContainerFromItem(first) is ComboBoxItem row) row.Focus();
            e.Handled = true;
        }
    }
}
