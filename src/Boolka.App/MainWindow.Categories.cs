using System.IO;
using System.Net.Http;
using System.Windows.Controls;

namespace Boolka;

public partial class MainWindow
{
    private IReadOnlyList<AddonCategory> categories = Array.Empty<AddonCategory>();
    private bool updatingCategories;
    private string? categoryWarning;

    private async Task LoadCategoriesAsync()
    {
        if (provider is not ICategoryProvider categoryProvider) return;
        try
        {
            categories = await categoryProvider.GetCategoriesAsync(lifetime.Token);
            categoryWarning = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            AppLog.Error("ui.operation.failed", ex);
            if (lifetime.IsCancellationRequested) return;
            // Сбой справочника не отменяет загрузку модов. До следующей проверки видны ID категорий.
            categoryWarning = UiText.Get("Не удалось загрузить названия категорий ESOUI. Доступен фильтр по номеру категории.");
        }
        UpdateCategoryOptions();
    }

    private void UpdateCategoryOptions()
    {
        updatingCategories = true;
        try
        {
            var options = categories.ToDictionary(x => x.Id);
            foreach (var id in catalog.Select(x => x.CategoryId).Append(preferences.CategoryId).Append(preferences.InstalledCategoryId).Where(x => x > 0).Distinct())
                options.TryAdd(id, new(id, UiText.Format($"Категория ESOUI №{id}")));
            var first = catalogMode ? new[] { new AddonCategory(0, UiText.Get("Все категории")) } : new[] { new AddonCategory(0, UiText.Get("Все категории")), new AddonCategory(-1, UiText.Get("Без категории")) };
            CategoryFilterBox.ItemsSource = first
                .Concat(options.Values.Select(x => x with { Name = CategoryNames.Display(x.Id, x.OriginalName) }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
            var selected = catalogMode ? preferences.CategoryId : preferences.InstalledCategoryId;
            CategoryFilterBox.SelectedItem = CategoryFilterBox.Items.Cast<AddonCategory>().First(x => x.Id == selected);
            System.Windows.Automation.AutomationProperties.SetName(CategoryFilterBox, UiText.Get("Категории"));
        }
        finally { updatingCategories = false; }
    }

    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingCategories || AddonList is null || CategoryFilterBox.SelectedItem is not AddonCategory selected) return;
        preferences = catalogMode ? preferences with { CategoryId = selected.Id } : preferences with { InstalledCategoryId = selected.Id };
        SavePreferences();
        Refresh();
    }
}
