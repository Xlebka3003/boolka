using System.Diagnostics;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    // Адрес задан владельцем приложения; открывается только после нажатия пользователя.
    internal const string DonationUrl = "https://boosty.to/xlebka/donate";

    private void OpenDonation(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("support.open.failed", ex);
            StatusLabel.Text = UiText.Get("Не удалось открыть страницу поддержки: ") + ex.Message;
        }
    }
}
