namespace Boolka;

internal static class FileSystemPaths
{
    // На Unix регистр может различать соседние каталоги. Для границ доступа используем
    // строгое сравнение, даже на macOS с регистронезависимым томом: безопаснее отказать.
    internal static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
