using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    private bool sidebarCollapsed;

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        sidebarCollapsed = !sidebarCollapsed;
        SidebarColumn.Width = new GridLength(sidebarCollapsed ? 76 : 218);

        var labelVisibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrand.Visibility = labelVisibility;
        // У навигации одна компактная раскладка: раскрытие добавляет только названия справа.
        SidebarTagline.Visibility = Visibility.Collapsed;
        SidebarLibraryLabel.Visibility = Visibility.Collapsed;
        SidebarOtherLabel.Visibility = Visibility.Collapsed;
        EsoGameLabel.Visibility = labelVisibility;
        AppUpdateLabel.Visibility = SupportLabel.Visibility = LaunchLabel.Visibility = labelVisibility;
        foreach (var button in new[] { EsoGameButton, AppUpdateButton, SupportButton, LaunchButton })
        {
            button.Width = sidebarCollapsed ? 44 : double.NaN;
            button.HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.Padding = sidebarCollapsed ? new Thickness(0) : new Thickness(8, 0, 8, 0);
        }
        WowGameLabel.Visibility = labelVisibility;
        SidebarFooter.Visibility = labelVisibility;
        System.Windows.Controls.Grid.SetColumn(SidebarToggle, sidebarCollapsed ? 0 : 1);
        SidebarToggle.HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SettingsButton.HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        SidebarToggle.Content = new LucideIcon { Icon = sidebarCollapsed ? "chevron-right" : "chevron-left" };
        UpdateSidebarCaptionWidth();
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle,
            sidebarCollapsed ? UiText.Get("Развернуть панель игр") : UiText.Get("Свернуть панель игр"));
    }

    private void ShowEsoLibrary(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        if (DetailPage.Visibility == Visibility.Visible)
        {
            ReturnToList();
        }

        StatusLabel.Text = catalogMode ? CatalogStatus() : InstalledStatus();
    }

    private void ShowWowComingSoon(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        StatusLabel.Text = UiText.Get("Поддержка World of Warcraft появится позже.");
    }
}
