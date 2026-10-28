using System.Windows;
using System.Windows.Controls;

namespace Boolka;

internal static class PackWindowUi
{
    public static TextBlock Text(string value, double size = 14) => new()
    {
        Text = value,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    public static TextBox Input(string value, int limit)
    {
        var box = new TextBox { Text = value, MaxLength = limit, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10) };
        box.SetResourceReference(Control.BackgroundProperty, "Theme.191E29");
        box.SetResourceReference(Control.ForegroundProperty, "Theme.E9EDF5");
        box.SetResourceReference(Control.BorderBrushProperty, "Theme.353C4E");
        return box;
    }

    public static Grid Setup(Window window, Window owner, string title)
    {
        if (owner.IsVisible) window.Owner = owner;
        window.Title = UiText.Get(title);
        window.Width = 660; window.Height = 720; window.MinWidth = 500; window.MinHeight = 480;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.SetResourceReference(Control.BackgroundProperty, "Theme.10131B");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.SetResourceReference(Panel.BackgroundProperty, "Theme.10131B");
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        window.Content = grid;
        DialogChrome.Apply(window);
        return grid;
    }
}

public sealed class PackExportWindow : Window
{
    public AddonPack? Result { get; private set; }

    public PackExportWindow(Window owner, IReadOnlyList<PackAddon> addons, int unresolved, AddonPack? initial = null)
    {
        var grid = PackWindowUi.Setup(this, owner, "Экспорт набора");
        var header = new StackPanel();
        header.Children.Add(PackWindowUi.Text(UiText.Get("Экспорт набора"), 24));
        header.Children.Add(PackWindowUi.Text(UiText.Get("Название набора")));
        var name = PackWindowUi.Input(initial?.Name ?? UiText.Get("Мои аддоны ESO"), 120); header.Children.Add(name);
        header.Children.Add(PackWindowUi.Text(UiText.Get("Автор (необязательно)")));
        var author = PackWindowUi.Input(initial?.Author ?? "", 120); header.Children.Add(author);
        header.Children.Add(PackWindowUi.Text(UiText.Get("Описание (необязательно)")));
        var description = PackWindowUi.Input(initial?.Description ?? "", 2000); description.Height = 60; description.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(description);
        header.Children.Add(PackWindowUi.Text(UiText.Format($"Не удалось найти на ESOUI: {unresolved}. Эти моды не войдут в набор.")));
        var all = new CheckBox { Content = UiText.Get("Выбрать все"), IsChecked = initial is null ? true : null, Margin = new Thickness(0, 0, 0, 12) };
        all.SetResourceReference(Control.ForegroundProperty, "Theme.E9EDF5"); header.Children.Add(all); grid.Children.Add(header);
        var list = new StackPanel();
        var items = addons.Select(x =>
        {
            var check = new CheckBox { Content = x.Name, Tag = x, IsChecked = initial is null || initial.Addons.Any(y => y.Id == x.Id), Margin = new Thickness(0, 5, 0, 5) };
            check.SetResourceReference(Control.ForegroundProperty, "Theme.E9EDF5"); list.Children.Add(check); return check;
        }).ToArray();
        all.Checked += (_, _) => { foreach (var check in items) check.IsChecked = true; };
        all.Unchecked += (_, _) => { foreach (var check in items) check.IsChecked = false; };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var error = PackWindowUi.Text(""); footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = UiText.Get("Отмена"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = UiText.Get("Сохранить набор") };
        save.Click += (_, _) =>
        {
            var pack = new AddonPack(1, "ESO", name.Text.Trim(), author.Text.Trim(), description.Text.Trim(),
                items.Where(x => x.IsChecked == true).Select(x => (PackAddon)x.Tag).ToArray());
            try { PackFile.Validate(pack); Result = pack; DialogResult = true; }
            catch (System.IO.InvalidDataException ex) { error.Text = ex.Message; }
        };
        actions.Children.Add(save); footer.Children.Add(actions); Grid.SetRow(footer, 2); grid.Children.Add(footer);
    }
}

public sealed class PackReviewWindow : Window
{
    public bool EditRequested { get; private set; }
    public PackReviewWindow(Window owner, AddonPack pack, PackPlan plan)
    {
        var grid = PackWindowUi.Setup(this, owner, "Импорт набора");
        var header = new StackPanel();
        header.Children.Add(PackWindowUi.Text(pack.Name, 24));
        if (pack.Author.Length > 0) header.Children.Add(PackWindowUi.Text(pack.Author));
        if (pack.Description.Length > 0) header.Children.Add(new ScrollViewer
        {
            Content = PackWindowUi.Text(pack.Description),
            MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        header.Children.Add(PackWindowUi.Text(UiText.Get("Добавляем только недостающие моды. Существующие папки и игровые настройки не изменяются.")));
        grid.Children.Add(header);
        var text = string.Join("\n\n", plan.Review);
        if (plan.Plan is { } ready)
            text += "\n\n" + UiText.Get("Будут установлены пакеты (включая библиотеки):") + "\n" + string.Join("\n", ready.Packages.Select(x => x.Name));
        var scroll = new ScrollViewer { Content = PackWindowUi.Text(text), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(PackWindowUi.Text(UiText.Get("Будут загружены текущие версии с ESOUI. Закройте ESO перед установкой.")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var edit = new Button { Content = UiText.Get("Изменить копию"), Margin = new Thickness(0, 0, 8, 0) };
        edit.Click += (_, _) => { EditRequested = true; DialogResult = false; };
        actions.Children.Add(edit);
        actions.Children.Add(new Button { Content = UiText.Get("Отмена"), IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) });
        var install = new Button { Content = UiText.Get("Установить недостающие"), IsEnabled = plan.Plan is { IsReady: true } };
        install.Click += (_, _) => DialogResult = true;
        actions.Children.Add(install); footer.Children.Add(actions); Grid.SetRow(footer, 2); grid.Children.Add(footer);
    }
}
