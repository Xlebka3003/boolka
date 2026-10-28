using System.IO;
using System.Text.Json;

namespace Boolka;

internal static class AdditionalTranslations
{
    private static readonly Dictionary<string, Dictionary<string, string>> Entries = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        // Словарь встроен в сборку: смена языка не требует сети или отдельных файлов.
        using var stream = typeof(AdditionalTranslations).Assembly.GetManifestResourceStream("Boolka.Translations.json")
            ?? throw new InvalidDataException("Missing embedded translation dictionary.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidDataException("Empty embedded translation dictionary.");
    }

    internal static bool IsTranslation(string text, string language) => Entries.TryGetValue(language, out var values)
        && values.Values.Contains(text, StringComparer.Ordinal);

    internal static bool Contains(string english, string language) => Entries.TryGetValue(language, out var values) && values.ContainsKey(english);

    internal static string Get(string english, string language) => Entries.TryGetValue(language, out var values)
        && values.TryGetValue(english, out var text) ? text : english;
}
