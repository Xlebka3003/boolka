using System.Net.Http;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    private Func<Task>? retryOperation;
    private string? retryFolder;

    private void BeginOperation()
    {
        AppLog.Info("operation.begin");
        retryOperation = null;
        RetryOperationButton.Visibility = Visibility.Collapsed;
        OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = true;
        OperationProgress.Value = 0;
    }

    private void DownloadProgressChanged(int index, int count, string name, long bytes, long? total)
    {
        OperationProgress.IsIndeterminate = total is not > 0;
        if (total is > 0)
            OperationProgress.Value = 100.0 * (index - 1 + Math.Clamp((double)bytes / total.Value, 0, 1)) / count;
        StatusLabel.Text = total is > 0
            ? UiText.Format($"Скачиваем {index} из {count} · {name}: {bytes / 1048576.0:0.0} из {total / 1048576.0:0.0} МБ")
            : UiText.Format($"Скачиваем {index} из {count} · {name}: {bytes / 1048576.0:0.0} МБ");
    }

    private void OperationFailed(Exception error, Func<Task> retry)
    {
        AppLog.Error("operation.failed", error);
        // Повтор запускает обычную операцию, сохраняя все проверки установщика.
        retryOperation = retry;
        retryFolder = currentFolder;
        RetryOperationButton.Visibility = Visibility.Visible;
        StatusLabel.Text = UiText.Get("Не удалось завершить операцию: ") + (error switch
        {
            HttpRequestException => UiText.Get("Сервер недоступен. Проверьте подключение и повторите попытку."),
            UnauthorizedAccessException => UiText.Get("Нет доступа к папке. Проверьте права и закройте программы, использующие файлы."),
            OperationCanceledException => UiText.Get("Время ожидания истекло. Повторите попытку."),
            _ => error.Message
        });
    }

    private void EndOperation()
    {
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Visibility = Visibility.Collapsed;
    }

    private async void RetryOperation(object sender, RoutedEventArgs e)
    {
        if (busy || retryOperation is not { } retry) return;
        if (!string.Equals(retryFolder, currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            retryOperation = null;
            RetryOperationButton.Visibility = Visibility.Collapsed;
            StatusLabel.Text = UiText.Get("Папка изменилась. Запустите нужную операцию заново.");
            return;
        }
        await retry();
    }
}
