using System.Globalization;

namespace Boolka;

internal sealed record UiLanguage(string Code, string NativeName, string Flag);

internal static class UiLanguages
{
    internal static readonly UiLanguage[] All =
    [
        new("en", "English", "gb"), new("ru", "Русский", "ru"),
        new("de", "Deutsch", "de"), new("fr", "Français", "fr"), new("es", "Español", "es")
    ];

    internal static string Normalize(string? code) => All.Any(x => x.Code == code) ? code! : "en";
    internal static CultureInfo Culture(string code) => CultureInfo.GetCultureInfo(code switch
    {
        "ru" => "ru-RU", "de" => "de-DE", "fr" => "fr-FR", "es" => "es-ES", _ => "en-US"
    });
}
