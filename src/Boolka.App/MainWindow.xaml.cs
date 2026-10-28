using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Boolka;

public partial class MainWindow : Window
{
    private readonly IGameProvider provider = new EsoProvider();
    private readonly SettingsStore settings;
    private readonly ArchiveDependencyCache dependencyCache;
    private readonly ArchiveDownloader downloader = new();
    private readonly AddonDetailsService detailsService = new();
    private CancellationTokenSource? detailsOperation;
    private CancellationTokenSource? imageOperation;
    private IReadOnlyList<Uri> galleryImages = Array.Empty<Uri>();
    private int galleryIndex;
    private double listOffset;
    private string? displayedDetailsKey;
    private string? loadedDescription;
    private ListBoxItem? pressedAddonRow;
    private Point pressedAddonPoint;
    private CancellationTokenSource? activeDownload;
    private DependencyPlan? selectedDependencyPlan;
    private string? currentFolder;
    private bool startupHandled;
    private IReadOnlyList<Addon> installed = Array.Empty<Addon>();
    private IReadOnlyList<Addon> catalog = Array.Empty<Addon>();
    private bool catalogMode;
    private bool busy;
    private bool installCommitting;
    private string? lastBackupDirectory;
    private DateTimeOffset? catalogLoadedAt;
    private readonly CancellationTokenSource lifetime = new();
    private string catalogEmptyMessage = UiText.Get("Нажмите «Каталог ESOUI», чтобы загрузить список аддонов.");

    public MainWindow() : this(new SettingsStore(), true) { }

    public MainWindow(SettingsStore settings, bool initializeOnLoad)
    {
        this.settings = settings;
        preferencesStore = new PreferencesStore(settings.PreferencesPath);
        dependencyCache = new ArchiveDependencyCache(Path.ChangeExtension(settings.PreferencesPath, "dependencies.json"));
        preferences = preferencesStore.Load();
        UiResources.Apply(preferences.Language);
        ThemeManager.Apply(preferences.Theme);
        CategoryIcons.ApplyMode(preferences.PreviewMode);
        InitializeComponent();
        WindowTheme.Attach(this);
        WindowWorkArea.Attach(this);
        StateChanged += (_, _) => MaximizeCaptionIcon.Icon = WindowState == WindowState.Maximized ? "copy" : "square";
        ToggleSidebar(this, new RoutedEventArgs());
        UiResources.Register("Открыть папку");
        OpenAddonsFolderButton.SetResourceReference(ContentControl.ContentProperty, UiText.ResourceKey("Открыть папку"));
        UiResources.Register("Повторить");
        RetryOperationButton.SetResourceReference(ContentControl.ContentProperty, UiText.ResourceKey("Повторить"));
        UiResources.Register("Запуск ESO и базы модов");
        LaunchButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, UiText.ResourceKey("Запуск ESO и базы модов"));
        if (LaunchButton.ContextMenu?.Items[0] is MenuItem launchMenu)
            launchMenu.SetResourceReference(HeaderedItemsControl.HeaderProperty, UiText.ResourceKey("Запуск ESO и базы модов"));
        CompactRows = preferences.CompactRows;
        ApplyCollectionScale();
        ApplyDescriptionScale();
        CatalogPresenceFilterBox.SelectedIndex = preferences.CatalogFilter;
        SortBox.SelectedIndex = preferences.SortIndex;
        UpdateCategoryOptions();
        UpdateSortDirection();
        initializingPreferences = false;
        UpdateViewButtons();
        SizeChanged += UpdateInterfaceScale;
        Deactivated += (_, _) => ResetGalleryGesture();
        if (initializeOnLoad) Loaded += InitializeFolder;
    }

    private async void InitializeFolder(object sender, RoutedEventArgs e)
    {
        if (startupHandled) return;
        startupHandled = true;
        var saved = settings.Load();
        if (saved.Folder is not null && Directory.Exists(saved.Folder))
        {
            if (await LoadFolderAsync(saved.Folder, false))
            {
                await FetchCatalogAsync(true, false);
                if (saved.Warning is not null) StatusLabel.Text += " · " + UiError.Detail(saved.Warning);
                await TryStartupAddonUpdateAsync();
                ShowPreviousCrashNotice();
                return;
            }
        }
        if (lifetime.IsCancellationRequested) return;
        var warning = saved.Warning;
        if (saved.Folder is not null) warning = UiText.Get("Сохранённая папка недоступна. Проверьте путь или выберите другую.");
        var dialog = new FolderSetupWindow(saved.Folder ?? SettingsStore.GetDefaultAddonFolder(), warning) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (await LoadFolderAsync(dialog.SelectedFolder, true)) await FetchCatalogAsync(true, false);
        }
        else StatusLabel.Text = UiText.Get("Папка не выбрана · Её можно указать кнопкой «Выбрать папку»");
        await TryStartupAddonUpdateAsync();
        ShowPreviousCrashNotice();
    }

    private void OpenAddonsFolder(object sender, RoutedEventArgs e)
    {
        if (currentFolder is null || !Directory.Exists(currentFolder))
        { StatusLabel.Text = UiText.Get("Сначала выберите папку ESO live\\AddOns кнопкой «Выбрать папку»."); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { currentFolder }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("ui.operation.failed", ex);
            StatusLabel.Text = UiError.Describe(ex);
        }
    }

    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = UiText.Get("Выберите папку ESO: live\\AddOns"), Multiselect = false };
        var initial = FolderSetupWindow.ExistingParent(currentFolder ?? SettingsStore.GetDefaultAddonFolder());
        if (initial is not null) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) != true) return;
        if (await LoadFolderAsync(dialog.FolderName, true)) await FetchCatalogAsync(true, false);
    }

    private async Task<bool> LoadFolderAsync(string folder, bool saveSelection, bool preserveView = false)
    {
        if (busy || lifetime.IsCancellationRequested) return false;
        SetBusy(true, UiText.Get("Читаем манифесты аддонов…"));
        try
        {
            AppLog.Info("scan.begin");
            var result = await provider.ScanAsync(folder);
            AppLog.Info($"scan.complete count={result.Addons.Count} skipped={result.Skipped}");
            if (lifetime.IsCancellationRequested) return false;
            installed = result.Addons;
            ApplyComparisons();
            currentFolder = folder;
            FolderLabel.Text = folder;
            if (!preserveView) catalogMode = false;
            Refresh();
            StatusLabel.Text = UiText.Format($"Найдено: {installed.Count} · Пропущено недоступных файлов или ссылок: {result.Skipped} · Файлы не изменены");
            if (saveSelection)
            {
                try { settings.Save(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    AppLog.Error("ui.operation.failed", ex);
                    StatusLabel.Text += UiText.Get(" · Не удалось сохранить выбор. При следующем запуске укажите папку заново.");
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("ui.operation.failed", ex);
            StatusLabel.Text = UiText.Get("Не удалось прочитать папку: ") + UiError.Describe(ex); return false;
        }
        finally { SetBusy(false); }
    }

    private void ApplyComparisons()
    {
        var results = AddonMatcher.Compare(installed, catalog, catalogLoadedAt.HasValue);
        var knownIds = results.Where(x => x.CatalogAddon?.CatalogId is > 0).Select(x => x.CatalogAddon!.CatalogId!.Value).ToHashSet();
        var unresolvedFolders = results.Where(x => x.CatalogAddon is null)
            .Select(x => Path.GetFileName(x.Addon.Path.TrimEnd('\\', '/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = results.Where(x => x.Status == UpdateStatus.UpdateAvailable && x.CatalogAddon?.CatalogId is > 0).Select(x => x.CatalogAddon!.CatalogId!.Value).ToHashSet();
        var health = results.Select(x => DependencyHealth.Evaluate(x.Addon with { Comparison = x }, installed)).ToList();
        var catalogHealth = results.Select((x, i) => (CatalogId: x.CatalogAddon?.CatalogId, Health: health[i])).Where(x => x.CatalogId.HasValue)
            .GroupBy(x => x.CatalogId!.Value).ToDictionary(x => x.Key, x => new DependencyHealthReport(
                x.SelectMany(y => y.Health.Missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                x.SelectMany(y => y.Health.Outdated).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                x.SelectMany(y => y.Health.Unknown).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        catalog = catalog.Select(x => x with
        {
            IsInstalled = x.CatalogId.HasValue && knownIds.Contains(x.CatalogId.Value),
            UnconfirmedInstallation = x.CatalogId.HasValue && !knownIds.Contains(x.CatalogId.Value)
                && x.CatalogFolders.Any(folder => unresolvedFolders.Contains(folder.Folder)),
            HasUpdate = x.CatalogId.HasValue && updates.Contains(x.CatalogId.Value),
            UpdatesIgnored = x.CatalogId.HasValue && (preferences.IgnoredIds ?? []).Contains(x.CatalogId.Value),
            DependencyHealth = x.CatalogId.HasValue ? catalogHealth.GetValueOrDefault(x.CatalogId.Value) : null
        }).ToList();
        installed = results.Select((result, i) => result.Addon with
        {
            Comparison = result,
            IsInstalled = true,
            HasUpdate = result.Status == UpdateStatus.UpdateAvailable,
            UpdatesIgnored = result.CatalogAddon?.CatalogId is long id && (preferences.IgnoredIds ?? []).Contains(id),
            DependencyHealth = health[i]
        }).ToList();
    }

    private DependencyPlan? BuildDependencyPlan(Addon addon, bool librariesOnly)
    {
        var root = addon.DisplayCatalog;
        if (root is null) return null;
        if (librariesOnly)
        {
            var actual = installed.Where(x => x.Comparison?.CatalogAddon?.CatalogId == root.CatalogId).ToList();
            if (actual.Count == 0) return null;
            root = root with { PackageModules = actual.Select(x => new CatalogModule(System.IO.Path.GetFileName(x.Path.TrimEnd('\\', '/')), x.BuildVersion, AddonRequirements.For(x))).ToList(), DependencyMetadataValid = actual.All(x => x.LocalMetadataComplete) };
        }
        return ArchiveDependencyPlanner.Build(root, catalog, installed, includeRoot: !librariesOnly);
    }

    private string InstalledStatus() => catalogLoadedAt.HasValue
        ? UiText.Format($"Установлено: {installed.Count} · Обновлений: {installed.Count(x => x.HasUpdate && !x.UpdatesIgnored)} · Игнорируются: {installed.Count(x => x.UpdatesIgnored)} · Каталог {catalogLoadedAt:HH:mm}")
        : UiText.Format($"Установлено: {installed.Count} · Нажмите «Пересканировать» для сравнения с ESOUI");

    private void ShowInstalled(object sender, RoutedEventArgs e) { catalogMode = false; Refresh(); StatusLabel.Text = InstalledStatus(); }

    private async void CheckUpdates(object sender, RoutedEventArgs e) => await RescanAsync(catalogMode);

    private async Task RescanAsync(bool showCatalog)
    {
        if (busy) return;
        // Перечитываем файлы и во вкладке каталога: наличие мода не определяется одним API.
        if (currentFolder is not null && !await LoadFolderAsync(currentFolder, false, preserveView: true)) return;
        await FetchCatalogAsync(true, showCatalog);
    }

    private async void ShowCatalog(object sender, RoutedEventArgs e) => await LoadCatalogAsync(false);

    private async void ReloadCatalog(object sender, RoutedEventArgs e) => await RescanAsync(true);

    private Task LoadCatalogAsync(bool forceReload) => FetchCatalogAsync(forceReload, true);

    private async Task FetchCatalogAsync(bool forceReload, bool showCatalog)
    {
        if (busy) return;
        catalogMode = showCatalog;
        Refresh();
        if (catalog.Count > 0 && !forceReload) { StatusLabel.Text = catalogMode ? CatalogStatus() : InstalledStatus(); return; }
        SetBusy(true, UiText.Get("Подключаемся к каталогу ESOUI…"));
        catalogEmptyMessage = UiText.Get("Загружаем каталог ESOUI…");
        if (catalogMode) EmptyText.Text = catalogEmptyMessage;
        try
        {
            catalog = await provider.GetCatalogAsync(lifetime.Token);
            await LoadCategoriesAsync();
            catalogLoadedAt = DateTimeOffset.Now;
            ApplyComparisons();
            Refresh();
            StatusLabel.Text = catalogMode ? CatalogStatus() : InstalledStatus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            AppLog.Error("ui.operation.failed", ex);
            var reason = ex switch
            {
                System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => UiText.Get("ESOUI ограничил частоту запросов. Повторите позже."),
                System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => UiText.Get("ESOUI отклонил доступ к каталогу (HTTP 403)."),
                System.Net.Http.HttpRequestException { StatusCode: { } code } => UiText.Format($"Сервер ESOUI ответил с ошибкой HTTP {(int)code}."),
                OperationCanceledException => UiText.Get("Сервер ESOUI не ответил за 20 секунд."),
                System.Text.Json.JsonException or InvalidDataException => UiText.Get("ESOUI вернул неизвестный формат каталога."),
                _ => UiText.Get("Не удалось подключиться к ESOUI. Проверьте интернет и настройки прокси.")
            };
            catalogEmptyMessage = reason + UiText.Get(" Нажмите «Обновить каталог», чтобы повторить.");
            Refresh();
            StatusLabel.Text = catalog.Count > 0 ? reason + UiText.Get(" Использованы сведения каталога, загруженные ранее в этой сессии.") : reason;
        }
        finally { SetBusy(false); }
    }

    private string CatalogStatus() => UiText.Format($"Каталог ESOUI · {catalog.Count} аддонов · Получен в {catalogLoadedAt:HH:mm}")
        + (categoryWarning is null ? "" : " · " + categoryWarning);

    private static string DetailsKey(Addon addon) => addon.PreviewId is > 0 ? "id:" + addon.PreviewId : "path:" + addon.Path;
    private static string ReadableDependencies(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"([^\s:()]+)>=([0-9]{1,128})", match => UiText.Format($"{match.Groups[1].Value} — сборка {match.Groups[2].Value} или новее"),
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    private ListBoxItem? FindClickedAddonRow(DependencyObject? source)
    {
        while (source is not null && source != AddonList)
        {
            if (source is ButtonBase { IsEnabled: true } or ScrollBar or Thumb) return null;
            if (source is ListBoxItem item) return ItemsControl.ItemsControlFromItemContainer(item) == AddonList ? item : null;
            source = source is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : source is Visual ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ClearSelectionOnBackground(object sender, MouseButtonEventArgs e)
    {
        if (!busy && !catalogMode && DetailPage.Visibility != Visibility.Visible
            && IsCollectionBackground(e.OriginalSource as DependencyObject))
        {
            AddonList.Focus();
            AddonList.UnselectAll();
        }
    }

    private bool IsCollectionBackground(DependencyObject? source) => IsCollectionInputContext(source, allowSelectionControls: false);

    private bool IsCollectionInputContext(DependencyObject? source, bool allowSelectionControls)
    {
        while (source is not null && source != this)
        {
            // Ввод текста и раскрытые меню сохраняют собственные сочетания клавиш.
            if (source is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem
                or ScrollBar or Thumb or MenuItem or System.Windows.Controls.ContextMenu) return false;
            if (!allowSelectionControls && (source is ButtonBase or ListBoxItem)) return false;
            source = source is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : source is Visual ? VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return source == this;
    }

    private bool selectingAddonRange;

    private void AddonPointerDown(object sender, MouseButtonEventArgs e)
    {
        selectingAddonRange = !catalogMode && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        pressedAddonRow = busy ? null : FindClickedAddonRow(e.OriginalSource as DependencyObject);
        pressedAddonPoint = e.GetPosition(AddonList);
    }

    private void AddonPointerUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = pressedAddonRow;
        pressedAddonRow = null;
        var selectionGesture = selectingAddonRange || !catalogMode && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        selectingAddonRange = false;
        // Ctrl/Shift обрабатывает сам ListBox: открытие страницы сбросило бы множественный выбор.
        if (selectionGesture) return;
        if (busy || pressed is null || FindClickedAddonRow(e.OriginalSource as DependencyObject) != pressed
            || pressed.DataContext is not Addon addon) return;
        var point = e.GetPosition(AddonList);
        if (Math.Abs(point.X - pressedAddonPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(point.Y - pressedAddonPoint.Y) > SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        AddonList.SelectedItem = addon;
        _ = OpenDetailsAsync(addon);
    }

    private async void AddonListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !catalogMode && !busy && AddonList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            await RemoveAddonsAsync(AddonList.SelectedItems.OfType<Addon>().ToArray());
            return;
        }
        if (SelectAllAddonsShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || busy || AddonList.SelectedItems.Count != 1 || AddonList.SelectedItem is not Addon addon) return;
        e.Handled = true;
        _ = OpenDetailsAsync(addon);
    }

    private bool SelectAllAddonsShortcut(Key key, ModifierKeys modifiers)
    {
        if (catalogMode || busy || key != Key.A || (modifiers & ModifierKeys.Control) == 0) return false;
        // После запуска фокус может быть на окне или кнопке. Следующая Delete
        // должна попасть в обработчик списка без предварительного щелчка мышью.
        AddonList.Focus();
        AddonList.SelectAll();
        return true;
    }

    private bool SelectAllFromWindow(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (DetailPage.Visibility == Visibility.Visible || GalleryOverlay.Visibility == Visibility.Visible
            || !IsCollectionInputContext(source, allowSelectionControls: true)) return false;
        return SelectAllAddonsShortcut(key, modifiers);
    }

    private async Task OpenDetailsAsync(Addon addon)
    {
        ResetGalleryGesture();
        detailsOperation?.Cancel(); imageOperation?.Cancel();
        detailsOperation?.Dispose();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        detailsOperation = operation;
        if (LibraryView.Visibility == Visibility.Visible) listOffset = FindVisual<ScrollViewer>(AddonList)?.VerticalOffset ?? 0;
        displayedDetailsKey = DetailsKey(addon);
        loadedDescription = null;
        galleryImages = Array.Empty<Uri>(); galleryIndex = 0;
        GalleryPanel.Visibility = GalleryOverlay.Visibility = Visibility.Collapsed;
        GalleryImage.Source = ExpandedImage.Source = null;
        LibraryView.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
        UpdateCollectionActions();
        ReloadCatalogButton.Visibility = CheckUpdatesButton.Visibility = Visibility.Collapsed;
        DetailPage.DataContext = addon;
        AddonSelected(AddonList, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        DetailScroll.ScrollToTop();
        BackButton.Focus();
        RetryDetailsButton.Visibility = Visibility.Collapsed;
        if (addon.PreviewId is not long id)
        { SetDetailLoadStatus(UiText.Get("Показано локальное описание. Загрузите каталог для полного описания и галереи ESOUI.")); return; }
        SetDetailLoadStatus(UiText.Get("Загружаем описание и изображения ESOUI…"));
        try
        {
            var details = await detailsService.GetAsync(id, operation.Token);
            if (detailsOperation != operation || operation.IsCancellationRequested || displayedDetailsKey != DetailsKey(addon)) return;
            loadedDescription = details.Description;
            DetailDescription.Text = CompactDescription(loadedDescription);
            galleryImages = details.Images;
            SetDetailLoadStatus(galleryImages.Count == 0 ? UiText.Get("У автора нет доступных изображений.") : "");
            if (galleryImages.Count > 0) await ShowGalleryImageAsync();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException or ArgumentException)
        {
            AppLog.Error("ui.operation.failed", ex);
            if (detailsOperation != operation || operation.IsCancellationRequested || displayedDetailsKey != DetailsKey(addon)) return;
            SetDetailLoadStatus(UiText.Get("Не удалось загрузить описание ESOUI. Показаны доступные сведения; можно повторить запрос или открыть страницу автора."));
            RetryDetailsButton.Visibility = Visibility.Visible;
        }
    }

    private async void RetryDetails(object sender, RoutedEventArgs e)
    {
        if (!busy && AddonList.SelectedItem is Addon addon) await OpenDetailsAsync(addon);
    }

    private void BackToList(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ReturnToList();
    }

    private void ReturnToList()
    {
        ResetGalleryGesture();
        detailsOperation?.Cancel(); imageOperation?.Cancel();
        displayedDetailsKey = null;
        loadedDescription = null;
        GalleryOverlay.Visibility = DetailPage.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        UpdateCollectionActions();
        ReloadCatalogButton.Visibility = catalogMode ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.Visibility = catalogMode ? Visibility.Collapsed : Visibility.Visible;
        AddonList.Focus();
        Dispatcher.BeginInvoke(new Action(() => FindVisual<ScrollViewer>(AddonList)?.ScrollToVerticalOffset(listOffset)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async Task ShowGalleryImageAsync()
    {
        imageOperation?.Cancel(); imageOperation?.Dispose();
        if (galleryImages.Count == 0 || detailsOperation is null || detailsOperation.IsCancellationRequested) return;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(detailsOperation.Token);
        var key = displayedDetailsKey;
        imageOperation = operation;
        GalleryPanel.Visibility = Visibility.Visible;
        GalleryImage.Source = ExpandedImage.Source = null;
        GalleryCounter.Text = ExpandedCounter.Text = UiText.Format($"{galleryIndex + 1} / {galleryImages.Count} · Загрузка…");
        PreviousImageButton.IsEnabled = NextImageButton.IsEnabled = galleryImages.Count > 1;
        try
        {
            var image = await detailsService.LoadImageAsync(galleryImages[galleryIndex], operation.Token);
            if (imageOperation != operation || operation.IsCancellationRequested || displayedDetailsKey != key) return;
            GalleryImage.Source = ExpandedImage.Source = image;
            GalleryCounter.Text = ExpandedCounter.Text = $"{galleryIndex + 1} / {galleryImages.Count}" + (image is null ? UiText.Get(" · Изображение недоступно") : "");
            GalleryImageButton.IsEnabled = image is not null;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or OperationCanceledException or ArgumentException or NotSupportedException or InvalidOperationException or System.IO.FileFormatException)
        {
            AppLog.Error("ui.operation.failed", ex);
            if (imageOperation != operation || operation.IsCancellationRequested) return;
            GalleryCounter.Text = ExpandedCounter.Text = UiText.Format($"{galleryIndex + 1} / {galleryImages.Count} · Изображение недоступно");
            GalleryImageButton.IsEnabled = false;
        }
    }

    private async void PreviousImage(object sender, RoutedEventArgs e)
    {
        await MoveGalleryAsync(-1);
    }

    private async void NextImage(object sender, RoutedEventArgs e)
    {
        await MoveGalleryAsync(1);
    }

    private async Task MoveGalleryAsync(int direction)
    {
        if (galleryImages.Count == 0)
        {
            return;
        }

        galleryIndex = (galleryIndex + direction + galleryImages.Count) % galleryImages.Count;
        await ShowGalleryImageAsync();
    }

    private void ExpandGallery(object sender, RoutedEventArgs e)
    {
        if (GalleryImage.Source is null)
        {
            return;
        }

        ResetGalleryGesture();
        GalleryOverlay.Visibility = Visibility.Visible;
        CloseGalleryButton.Focus();
    }

    private void CloseGallery(object sender, RoutedEventArgs e)
    {
        ResetGalleryGesture();
        GalleryOverlay.Visibility = Visibility.Collapsed;
        GalleryImageButton.Focus();
    }

    private void SetDetailLoadStatus(string text)
    {
        DetailLoadStatus.Text = text;
        DetailLoadStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectAllFromWindow(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SortDirectionButton.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (GalleryOverlay.Visibility == Visibility.Visible)
        {
            CloseGallery(sender, e);
            e.Handled = true;
        }
        else if (DetailPage.Visibility == Visibility.Visible && !busy)
        {
            BackToList(sender, e);
            e.Handled = true;
        }
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        ResetGalleryGesture();
        lifetime.Cancel();
        detailsOperation?.Cancel(); imageOperation?.Cancel();
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (installCommitting)
        {
            e.Cancel = true;
            StatusLabel.Text = UiText.Get("Дождитесь завершения операции или отката — сохраняем файлы безопасно.");
        }
        base.OnClosing(e);
    }

    private void SetBusy(bool value, string? message = null)
    {
        busy = value;
        BackButton.IsEnabled = RetryDetailsButton.IsEnabled = !value;
        DetailInstallButton.IsEnabled = !value && (AddonList.SelectedItem as Addon)?.CanInstall == true;
        SearchBox.IsEnabled = !value;
        AddonList.IsEnabled = !value;
        LibrariesButton.IsEnabled = !value && CanRepairLibraries();
        DownloadZipButton.IsEnabled = !value && CanDownloadSelectedZip();
        SortBox.IsEnabled = StatusFilterBox.IsEnabled = !value;
        ChooseFolderButton.IsEnabled = ReloadCatalogButton.IsEnabled = !value;
        CheckUpdatesButton.IsEnabled = !value && currentFolder is not null;
        InstalledButton.IsEnabled = CatalogButton.IsEnabled = !value;
        CatalogPresenceFilterBox.IsEnabled = CardsButton.IsEnabled = CompactButton.IsEnabled = !value;
        CategoryFilterBox.IsEnabled = !value;
        ExportPackButton.IsEnabled = ImportPackButton.IsEnabled = !value && currentFolder is not null;
        SortDirectionButton.IsEnabled = !value;
        SettingsButton.IsEnabled = !value;
        UpdateCollectionActions();
        if (message is not null) StatusLabel.Text = message;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) { if (AddonList is not null) Refresh(); }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddonList is null) return;
        SavePreferences();
        Refresh();
    }

    private void Refresh()
    {
        UpdateCategoryOptions();
        var previous = AddonList.SelectedItem as Addon;
        var selectedPaths = AddonList.SelectedItems.OfType<Addon>().Where(item => item.Path.Length > 0)
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchBox.Text.Trim();
        var source = catalogMode ? catalog : installed;
        var filter = catalogMode ? StatusFilter.All : (StatusFilter)Math.Max(0, StatusFilterBox.SelectedIndex);
        var presence = catalogMode ? (CatalogPresenceFilter)Math.Max(0, CatalogPresenceFilterBox.SelectedIndex) : CatalogPresenceFilter.All;
        var selectedCategory = catalogMode ? preferences.CategoryId : preferences.InstalledCategoryId;
        long Category(Addon addon) => catalogMode ? addon.CategoryId : addon.Comparison?.CatalogAddon?.CategoryId ?? 0;
        var categorySource = selectedCategory == 0 ? source : source.Where(x => selectedCategory == -1 ? Category(x) <= 0 : Category(x) == selectedCategory).ToArray();
        var sort = (AddonSort)Math.Max(0, SortBox.SelectedIndex);
        var descending = preferences.DescendingSort ?? true;
        // Query принимает разворот относительно своего исходного порядка, интерфейс хранит общее направление.
        var reverse = sort == AddonSort.Name ? descending : !descending;
        var filtered = AddonQuery.Apply(categorySource, query, sort, filter, presence, reverse);
        UpdateSortDirection();
        AddonList.SelectionMode = catalogMode ? SelectionMode.Single : SelectionMode.Extended;
        AddonList.ItemsSource = filtered;
        StatusFilterBox.Visibility = catalogMode ? Visibility.Collapsed : Visibility.Visible;
        CatalogPresenceFilterBox.Visibility = catalogMode ? Visibility.Visible : Visibility.Collapsed;
        CategoryFilterBox.Visibility = Visibility.Visible;
        UpdateCollectionActions();
        ResultsLabel.Text = UiText.Format($"Показано {filtered.Count} из {source.Count}");
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = source.Count > 0 ? UiText.Get("Ничего не найдено") : catalogMode ? UiText.Get("Каталог ESOUI") : UiText.Get("Начнём с вашей коллекции");
        ReloadCatalogButton.Visibility = catalogMode && DetailPage.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.Visibility = !catalogMode && DetailPage.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = !busy && currentFolder is not null;
        EmptyText.Text = source.Count > 0 ? UiText.Get("Попробуйте изменить поисковый запрос или фильтр статуса.") : catalogMode ? catalogEmptyMessage : UiText.Get("Выберите папку AddOns. Boolka прочитает названия, версии и зависимости из манифестов.");
        InstalledButton.Background = catalogMode ? Brushes.Transparent : ThemeManager.Brush("#353044");
        CatalogButton.Background = catalogMode ? ThemeManager.Brush("#353044") : Brushes.Transparent;
        if (filtered.Count > 0) AddonList.SelectedItem = filtered.FirstOrDefault(x => catalogMode ? x.CatalogId == previous?.CatalogId : x.Path == previous?.Path) ?? filtered[0];
        else if (DetailPage.Visibility == Visibility.Visible) ReturnToList();
        if (!catalogMode && selectedPaths.Count > 1)
        {
            var retained = filtered.Where(item => selectedPaths.Contains(item.Path)).ToArray();
            if (retained.Length > 0)
            {
                AddonList.SelectedItems.Clear();
                foreach (var item in retained) AddonList.SelectedItems.Add(item);
            }
        }
    }

    private void AddonSelected(object sender, SelectionChangedEventArgs e)
    {
        var addon = AddonList.SelectedItem as Addon;
        if (DetailPage.Visibility == Visibility.Visible && addon is not null && displayedDetailsKey != DetailsKey(addon)) ReturnToList();
        DetailTitle.Text = addon?.DisplayName ?? UiText.Get("Всё под рукой");
        DetailPage.DataContext = addon;
        DetailAuthor.Text = addon?.Author ?? "";
        DetailDescription.Text = CompactDescription(addon is not null && displayedDetailsKey == DetailsKey(addon) && loadedDescription is not null ? loadedDescription : addon?.Description ?? UiText.Get("Выберите аддон в списке, чтобы посмотреть сведения о нём."));
        DetailInstallButton.IsEnabled = !busy && addon?.CanInstall == true;
        DetailDependencies.Children.Clear();
        var checkedModules = dependencyCache.Find(addon?.DisplayCatalog);
        DetailDependencySource.Text = addon is null ? "" : UiText.Get(DependencyPresenter.Source(addon, installed, checkedModules));
        var dependencyRows = addon is null ? [] : DependencyPresenter.Build(addon, installed, checkedModules);
        if (dependencyRows.Count == 0)
            DetailDependencies.Children.Add(new TextBlock { Text = UiText.Get(addon?.IsInstalled == true || checkedModules is not null ? "Обязательные зависимости не указаны." : "Зависимости будут проверены при установке"), TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        foreach (var row in dependencyRows)
        {
            if (DetailDependencies.Children.Count > 0)
            {
                var separator = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
                separator.SetResourceReference(Border.BackgroundProperty, "Theme.353044");
                DetailDependencies.Children.Add(separator);
            }
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var requirement = new TextBox
            {
                Style = (Style)FindResource("SelectableDetailText"),
                Text = ReadableDependencies(row.Requirement), TextWrapping = TextWrapping.Wrap,
                FontSize = 14, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center
            };
            line.Children.Add(requirement);
            var state = new TextBox
            {
                Style = (Style)FindResource("SelectableDetailText"),
                Text = UiText.Get(row.Status), TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Right,
                Foreground = ThemeManager.Brush(row.NeedsAttention ? "#F1BDA9" : "#9AA3B5"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1);
            line.Children.Add(state);
            DetailDependencies.Children.Add(line);
        }
        DetailDependencyHealth.Text = ReadableDependencies(addon?.DependencyHealth?.Details ?? (addon?.IsInstalled == true ? UiText.Get("Не удалось проверить библиотеки. Перечитайте папку аддонов.") : ""));
        if (addon?.UnconfirmedInstallation == true)
            DetailDependencyHealth.Text = UiText.Get("Папка аддона уже существует, но её проект не определён однозначно. Повторная установка заблокирована; проверьте установленный аддон.");
        if (addon?.UnconfirmedInstallation != true && addon?.DependencyHealth is { HasProblems: false, Unknown.Count: 0 })
            DetailDependencyHealth.Text = "";
        DetailDependencyHealth.Visibility = string.IsNullOrWhiteSpace(DetailDependencyHealth.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        DetailDependencyHealth.Foreground = ThemeManager.Brush(addon?.DependencyStatusColor ?? "#9AA3B5");
        DetailDownloads.Text = addon?.DownloadsText ?? "—";
        DetailUpdated.Text = addon?.UpdatedText ?? "—";
        var comparison = addon?.Comparison;
        var remote = comparison?.CatalogAddon;
        DetailVersion.Text = addon is null ? "—" : !catalogMode && addon.HasUpdate && remote is not null
            ? UiText.Get("Установлена") + ": " + addon.VersionLabel + "\n" + UiText.Get("Доступна") + ": " + remote.VersionLabel : addon.VersionLabel;
        MatchedPageButton.Visibility = !catalogMode && remote?.WebUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        selectedDependencyPlan = addon is null ? null : BuildDependencyPlan(addon, addon.IsInstalled);
        DetailDependencyPlan.Text = selectedDependencyPlan is null ? UiText.Get("Нужен каталог и подтверждённое сопоставление проекта.")
            : addon?.DependencyHealth is { Unknown.Count: > 0 } ? UiText.Get("Автоматическое исправление недоступно: часть зависимостей не удалось проверить.")
            : !selectedDependencyPlan.IsReady ? UiText.Get("Автозагрузка невозможна:\n") + string.Join("\n", selectedDependencyPlan.Issues.Take(8).Select(UiError.Detail))
            : addon!.IsInstalled ? ""
            : "";
        DetailDependencyPlan.Visibility = string.IsNullOrWhiteSpace(DetailDependencyPlan.Text) ? Visibility.Collapsed : Visibility.Visible;
        DetailOpenFolderButton.Visibility = catalogMode ? Visibility.Visible : Visibility.Collapsed;
        DetailOpenFolderButton.Content = UiText.Get("Открыть папку");
        DetailOpenFolderButton.IsEnabled = !busy && SelectedAddonFolder(addon) is not null;
        LibrariesButton.Visibility = addon?.IsInstalled == true && addon.HasDependencyProblems && selectedDependencyPlan is not null ? Visibility.Visible : Visibility.Collapsed;
        LibrariesButton.IsEnabled = !busy && CanRepairLibraries();
        LibrariesButton.Background = CanRepairLibraries() ? ThemeManager.Brush("#FFB86C") : Brushes.Transparent;
        LibrariesButton.Foreground = ThemeManager.Brush(CanRepairLibraries() ? "#14121D" : "#E9EDF5");
        DownloadZipButton.IsEnabled = !busy && CanDownloadSelectedZip();
        OpenButton.Content = catalogMode ? "ESOUI ↗" : UiText.Get("Открыть папку");
        OpenButton.IsEnabled = addon is not null && (catalogMode ? addon.WebUrl is not null : Directory.Exists(addon.Path));
    }

    private string? SelectedAddonFolder(Addon? addon)
    {
        var local = addon?.CatalogId is long id
            ? installed.FirstOrDefault(item => item.Comparison?.CatalogAddon?.CatalogId == id) : addon;
        return local is { IsInstalled: true } && Directory.Exists(local.Path) ? local.Path : null;
    }

    private void OpenDetailFolder(object sender, RoutedEventArgs e)
    {
        var folder = SelectedAddonFolder(AddonList.SelectedItem as Addon);
        if (folder is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { StatusLabel.Text = UiText.Get("Не удалось открыть: ") + UiError.Describe(ex); }
    }

    private void OpenSelected(object sender, RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is not Addon addon) return;
        try
        {
            if (catalogMode && addon.WebUrl is not null) Process.Start(new ProcessStartInfo(addon.WebUrl) { UseShellExecute = true });
            else if (!catalogMode && Directory.Exists(addon.Path)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { addon.Path }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusLabel.Text = UiText.Get("Не удалось открыть: ") + UiError.Describe(ex); }
    }

    private void OpenMatchedPage(object sender, RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is not Addon { Comparison.CatalogAddon.WebUrl: { } url }) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusLabel.Text = UiText.Get("Не удалось открыть страницу: ") + UiError.Describe(ex); }
    }


    private bool CanRepairLibraries() => selectedDependencyPlan is { IsReady: true, Packages.Count: > 0 }
        && AddonList.SelectedItem is Addon { DependencyHealth: { HasProblems: true, Unknown.Count: 0 } };

    private void DownloadSelectedZip(object sender, RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is Addon addon) DownloadAddon(new Button { DataContext = addon }, e);
    }

    private bool CanDownloadSelectedZip() => AddonList.SelectedItem is Addon { CanDownload: true } addon
        && ZipDownloadPlan.Build(addon, catalog, installed) is { IsReady: true };

    private async void InstallAddon(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { DataContext: Addon { CanInstall: true } addon }) return;
        AddonList.SelectedItem = addon;
        await ExecuteInstallationAsync(addon, false);
    }

    private void OpenBackup(object sender, RoutedEventArgs e)
    {
        if (lastBackupDirectory is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { lastBackupDirectory }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusLabel.Text = UiText.Get("Не удалось открыть резервную копию: ") + UiError.Describe(ex); }
    }

    private async Task ExecuteInstallationAsync(Addon? chosen, bool librariesOnly, bool reinstall = false, bool batch = false, PackImportApproval? packImport = null)
    {
        if (currentFolder is null || !Directory.Exists(currentFolder))
        { StatusLabel.Text = UiText.Get("Сначала выберите папку ESO live\\AddOns кнопкой «Выбрать папку»."); return; }
        var target = currentFolder;
        if (!System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(target)).Equals("AddOns", StringComparison.OrdinalIgnoreCase))
        { StatusLabel.Text = UiText.Get("Для установки выберите папку с именем AddOns внутри ESO live."); return; }
        var id = chosen?.PreviewId;
        if (!batch && id is not > 0) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeDownload = operation;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Boolka-install-download-" + Guid.NewGuid().ToString("N"));
        var archives = packImport?.Archives is { } preparedArchives ? new Dictionary<long, string>(preparedArchives) : new Dictionary<long, string>();
        SetBusy(true, UiText.Get("Проверяем установленные аддоны и свежий каталог…"));
        BeginOperation();
        try
        {
            Directory.CreateDirectory(directory);
            installed = (await provider.ScanAsync(target)).Addons;
            catalog = await provider.GetCatalogAsync(operation.Token);
            catalogLoadedAt = DateTimeOffset.Now;
            ApplyComparisons();
            DependencyPlan? plan;
            IReadOnlyList<string> skipped = Array.Empty<string>();
            if (packImport is not null)
            {
                var packPlan = PackPlanner.Build(packImport.Pack, catalog, installed, (preferences.IgnoredIds ?? []).ToHashSet());
                if (!target.Equals(packImport.TargetFolder, StringComparison.OrdinalIgnoreCase) || packPlan.Fingerprint != (packImport.CatalogFingerprint ?? packImport.Fingerprint))
                    throw new InvalidDataException(UiText.Get("Состав или версии изменились. Нажмите «Повторить» и проверьте набор заново."));
                var prepared = await PackArchivePlanner.PrepareAsync(packImport.Pack, catalog, installed,
                    (preferences.IgnoredIds ?? []).ToHashSet(), archives,
                    package => DownloadPackArchiveAsync(package, directory, operation.Token), operation.Token);
                if (prepared.Plan.Fingerprint != packImport.Fingerprint)
                    throw new InvalidDataException(UiText.Get("Состав или версии изменились. Нажмите «Повторить» и проверьте набор заново."));
                catalog = prepared.Catalog;
                ApplyComparisons();
                plan = prepared.Plan.Plan;
                skipped = prepared.Plan.Skipped;
                if (plan is null) { StatusLabel.Text = UiText.Get("Недостающих пакетов для установки нет."); return; }
            }
            else if (batch)
            {
                var batchPlan = BatchPlanBuilder.Build(installed, catalog, librariesOnly, (preferences.IgnoredIds ?? []).ToHashSet());
                plan = batchPlan.Plan;
                skipped = batchPlan.Skipped;
                if (plan is null)
                {
                    StatusLabel.Text = UiText.Get("Подходящих пакетов нет. ") + string.Join("; ", skipped.Take(5));
                    return;
                }
            }
            else
            {
                var root = catalog.SingleOrDefault(x => x.CatalogId == id) ?? throw new InvalidDataException(UiText.Get("Проект больше не найден в каталоге."));
                if (librariesOnly && root.DependencyHealth is not { HasProblems: true, Unknown.Count: 0 })
                    throw new InvalidDataException(UiText.Get("Нет подтверждённых недостающих библиотек либо есть непроверенные зависимости. Подробности — в сведениях."));
                if (!librariesOnly && !root.CanInstall && !reinstall) throw new InvalidDataException(UiText.Get("Аддон уже установлен; подтверждённого обновления нет."));
                if (reinstall && !root.IsInstalled) throw new InvalidDataException(UiText.Get("Установленный проект больше не подтверждён."));
                plan = BuildDependencyPlan(root, librariesOnly);
            }
            if (plan is null || !plan.IsReady) throw new InvalidDataException(UiText.Get("Не удалось определить обязательные зависимости: ") + string.Join("; ", plan?.Issues ?? Array.Empty<string>()));
            if (plan.Packages.Count == 0) { StatusLabel.Text = UiText.Get("Все обязательные библиотеки уже установлены."); return; }
            Directory.CreateDirectory(directory);
            if (batch)
            {
                await InstallBatchIndividuallyAsync(plan, skipped, librariesOnly, target, directory, archives, operation, packImport);
                return;
            }
            plan = await PrepareArchivesAsync(plan, directory, archives, operation, discoverDependencies: packImport is null);
            operation.Token.ThrowIfCancellationRequested();
            OperationProgress.IsIndeterminate = true;
            // Нажатие установки, обновления или переустановки сразу запускает операцию.
            // Проверки архива, резервная копия и откат остаются внутри SafeInstaller.
            installCommitting = true;
            StatusLabel.Text = UiText.Get("Проверяем и устанавливаем архивы. Дождитесь завершения…");
            var updatedCount = plan.Packages.Count(x => x.IsInstalled);
            var installedCount = plan.Packages.Count - updatedCount;
            EnsureStartupUpdateGameClosed();
            var result = await new SafeInstaller().InstallAsync(plan, archives, target, operation.Token);
            lastBackupDirectory = result.BackupDirectory;
            OpenBackupButton.Visibility = Visibility.Visible;
            StatusLabel.Text = UiText.Format($"Готово · Установлено: {installedCount} · Обновлено: {updatedCount} · Пропущено: {skipped.Count}");
            try
            {
                installed = (await provider.ScanAsync(target)).Addons;
                ApplyComparisons();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AppLog.Error("ui.operation.failed", ex);
                StatusLabel.Text += UiText.Get(" · Установка завершена, но список не обновлён. Нажмите «Пересканировать».");
            }
        }
        catch (OperationCanceledException ex)
        { if (!lifetime.IsCancellationRequested) OperationFailed(ex, packImport is null ? () => ExecuteInstallationAsync(chosen, librariesOnly, reinstall, batch) : () => PreviewPackImportAsync(packImport.Pack)); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            AppLog.Error("ui.operation.failed", ex);
            OperationFailed(ex, packImport is null ? () => ExecuteInstallationAsync(chosen, librariesOnly, reinstall, batch) : () => PreviewPackImportAsync(packImport.Pack));
        }
        finally
        {
            installCommitting = false;
            activeDownload = null;
            foreach (var file in archives.Values) { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            EndOperation();
            SetBusy(false);
            Refresh();
        }
    }

    private async void DownloadAddon(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { DataContext: Addon addon } || !addon.CanDownload || addon.PreviewId is not long id) return;
        AddonList.SelectedItem = addon;
        var plan = ZipDownloadPlan.Build(addon, catalog, installed);
        if (plan is null || !plan.IsReady) { StatusLabel.Text = UiText.Get("Не удалось определить обязательные зависимости. Подробности — в сведениях об аддоне."); return; }
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Сохранить ZIP-архив аддона"),
            Filter = UiText.Get("ZIP-архив (*.zip)|*.zip"),
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = DownloadMetadata.SuggestedFileName(plan.Root.Name, plan.Root.Version, id)
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExecuteDependencyDownloadAsync(plan, System.IO.Path.GetDirectoryName(dialog.FileName)!, dialog.FileName);
    }

    private async void DownloadLibraries(object sender, RoutedEventArgs e)
    {
        if (busy || !CanRepairLibraries() || AddonList.SelectedItem is not Addon addon) return;
        await ExecuteInstallationAsync(addon, true);
    }

    private async Task ExecuteDependencyDownloadAsync(DependencyPlan plan, string directory, string? rootDestination)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeDownload = operation;
        BeginOperation();
        SetBusy(true, UiText.Format($"Подготавливаем архивы: {plan.Packages.Count}…"));
        try
        {
            var progress = new Progress<BatchDownloadProgress>(value =>
            {
                if (activeDownload != operation || operation.IsCancellationRequested) return;
                DownloadProgressChanged(value.Index, value.Count, value.Name, value.Bytes, value.Total);
            });
            var result = await new DependencyBatchDownloader(downloader).DownloadAsync(plan, directory, rootDestination, progress, operation.Token, catalog, installed);
            StatusLabel.Text = UiText.Format($"Архивов сохранено: {result.Files.Count} · {result.LibraryDirectory ?? rootDestination} · Установка не выполнялась");
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            AppLog.Error("ui.operation.failed", ex);
            if (!lifetime.IsCancellationRequested)
                OperationFailed(ex, () => ExecuteDependencyDownloadAsync(plan, directory, rootDestination));
        }
        finally
        {
            activeDownload = null;
            EndOperation();
            SetBusy(false);
        }
    }
}

