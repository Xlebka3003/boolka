namespace Boolka;

public static class ThemePalette
{
    public static bool IsLight { get; set; }
    public static bool IsDracula { get; set; }
    // Свойство задаёт только квадратную форму, независимо от цвета.
    public static bool IsClassic { get; set; }

    public static string Map(string color) => IsDracula ? Dracula(color) : IsLight ? Warm(color) : Graphite(color);

    private static string Dracula(string color) => color.ToUpperInvariant() switch
    {
        "#10131B" or "#10131BFF" => "#282A36",
        "#151923" => "#21222C",
        "#191E29" or "#1B202C" or "#1B2030" or "#2B273D" => "#303240",
        "#222735" or "#252B39" or "#242A38" or "#29253B" or "#26233A" or "#353044" => "#44475A",
        "#252A36" or "#262D3C" => "#565970",
        "#353C4E" or "#454D62" => "#6272A4",
        "#E9EDF5" or "#E0E5EF" => "#F8F8F2",
        "#9AA3B5" or "#737D90" => "#C4C7DE",
        "#14121D" => "#282A36",
        "#A995EC" or "#B8A3F2" => "#BD93F9",
        "#645B83" => "#565970",
        "#C8B7FF" or "#C7B9FF" or "#B9A9F4" => "#8BE9FD",
        "#B7A4FF" or "#A795EF" => "#FF79C6",
        "#7DDDC0" or "#94D6AE" => "#50FA7B",
        "#F28B93" or "#D66F7B" => "#FF8B8B",
        "#F1BDA9" => "#FFB86C",
        "#E8C17A" => "#F1FA8C",
        "#F010131B" => "#F0282A36",
        "#8098A4BD" => "#806272A4",
        "#B0B7A4FF" => "#B0BD93F9",
        "#E0B7A4FF" => "#E0BD93F9",
        _ => color
    };

    private static string Graphite(string color) => color.ToUpperInvariant() switch
    {
        "#10131B" or "#10131BFF" => "#202124",
        "#151923" => "#242529",
        "#191E29" or "#1B202C" => "#292B30",
        "#1B2030" => "#2D2F35",
        "#222735" or "#252B39" or "#242A38" => "#35373F",
        "#29253B" or "#26233A" or "#353044" => "#403A50",
        "#2B273D" => "#37343F",
        "#252A36" or "#262D3C" => "#4A4C55",
        "#353C4E" or "#454D62" => "#777986",
        "#E9EDF5" or "#E0E5EF" => "#E8EAED",
        "#9AA3B5" => "#B5B8C2",
        "#737D90" => "#AAADBA",
        "#14121D" => "#14121D",
        "#A995EC" or "#B8A3F2" => "#C4B2EB",
        "#645B83" => "#635975",
        "#F010131B" => "#F0202124",
        _ => color
    };

    private static string Warm(string color) => color.ToUpperInvariant() switch
    {
        "#10131B" or "#10131BFF" => "#F4F1EB",
        "#151923" => "#EAE5DD",
        "#191E29" or "#1B202C" => "#FAF8F4",
        "#1B2030" => "#FAF8F4",
        "#222735" or "#252B39" or "#242A38" => "#E4DED6",
        "#29253B" or "#26233A" or "#353044" => "#E6DCF0",
        "#2B273D" => "#E9E2EC",
        "#252A36" or "#262D3C" => "#B9B0A8",
        "#353C4E" or "#454D62" => "#837A72",
        "#E9EDF5" or "#E0E5EF" => "#292722",
        "#9AA3B5" => "#655E56",
        "#737D90" => "#655E56",
        "#14121D" => "#292333",
        "#A995EC" or "#B8A3F2" => "#D7C5ED",
        "#645B83" => "#D4CADC",
        "#C8B7FF" or "#C7B9FF" or "#B9A9F4" or "#B7A4FF" or "#A795EF" => "#69468F",
        "#7DDDC0" or "#94D6AE" => "#096A4A",
        "#F28B93" or "#D66F7B" => "#AB2740",
        "#F1BDA9" => "#93431F",
        "#E8C17A" => "#7D560D",
        "#F010131B" => "#F0F4F1EB",
        "#8098A4BD" => "#80655E56",
        "#B0B7A4FF" => "#B069468F",
        "#E0B7A4FF" => "#E069468F",
        _ => color
    };
}
