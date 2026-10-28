using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Boolka;

/// <summary>Локализованное сообщение пользователю; исходная ошибка остаётся в диагностическом журнале.</summary>
internal static class UiError
{
    internal static string Describe(Exception error)
    {
        var code = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(error.GetType().Name + ":" + error.Message)))[..8];
        AppLog.Error("ui.error." + code, error);
        if (UiText.English.ContainsKey(error.Message)) return UiText.Get(error.Message);
        if (AdditionalTranslations.IsTranslation(error.Message, UiText.Language)
            || UiText.Language == "en" && UiText.English.Values.Contains(error.Message)) return error.Message;
        if (UiText.Language == "ru" && error.Message.Any(character => character is >= 'А' and <= 'я')) return error.Message;
        var message = error switch
        {
            UnauthorizedAccessException => "Нет доступа к папке. Проверьте права и закройте программы, использующие файлы.",
            HttpRequestException => "Сервер недоступен. Проверьте подключение и повторите попытку.",
            OperationCanceledException => "Время ожидания истекло. Повторите попытку.",
            InvalidDataException or System.Text.Json.JsonException => "Данные не прошли проверку. Операция остановлена.",
            IOException => "Файл или папка недоступны. Проверьте путь, права доступа и занятость файлов.",
            ArgumentException => "Некорректные параметры операции. Проверьте путь и настройки.",
            _ => "Не удалось выполнить системную операцию."
        };
        return UiText.Get(message) + " " + UiText.Format($"Для подробностей сохраните диагностический журнал. Код: {code}.");
    }

    internal static string Detail(string message)
    {
        if (UiText.English.ContainsKey(message)) return UiText.Get(message);
        if (AdditionalTranslations.IsTranslation(message, UiText.Language)) return message;
        var source = UiText.English.FirstOrDefault(pair => pair.Value == message);
        return source.Key is not null ? UiText.Get(source.Key) : Describe(new InvalidDataException(message));
    }
}
