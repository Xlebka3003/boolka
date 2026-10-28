using System.Globalization;

namespace Boolka;

internal sealed record UiLanguage(string Code, string NativeName, string Flag);

internal static class UiLanguages
{
    internal static readonly UiLanguage[] All =
    [
        new("en", "English", "gb"), new("ru", "Русский", "ru"),
        new("de", "Deutsch", "de"), new("fr", "Français", "fr"), new("es", "Español", "es"),
        new("pt-BR", "Português (Brasil)", "br"), new("pl", "Polski", "pl"),
        new("zh-CN", "简体中文", "cn"), new("ja", "日本語", "jp")
    ];

    internal static string Normalize(string? code) => All.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Code ?? "en";
    internal static CultureInfo Culture(string code) => CultureInfo.GetCultureInfo(code switch
    {
        "ru" => "ru-RU", "de" => "de-DE", "fr" => "fr-FR", "es" => "es-ES", "pt-BR" => "pt-BR", "pl" => "pl-PL", "zh-CN" => "zh-CN", "ja" => "ja-JP", _ => "en-US"
    });
}
