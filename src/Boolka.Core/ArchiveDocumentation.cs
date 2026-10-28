using System.IO;
using System.IO.Compression;
using System.Text;

namespace Boolka;

internal static class ArchiveDocumentation
{
    public const long MaxTotalBytes = 1024 * 1024;
    private const int MaxFileBytes = 256 * 1024;
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme", "license", "licence", "copying", "changelog", "changes", "authors", "obsoleteFolder"
    };

    public static bool CanDiscard(ZipArchiveEntry entry, string relative)
    {
        var name = relative.Split('/')[^1];
        var extension = Path.GetExtension(name);
        if (!Names.Contains(Path.GetFileNameWithoutExtension(name))
            || extension.Length > 0 && extension.ToLowerInvariant() is not (".txt" or ".md" or ".rst")
            || entry.Length < 0 || entry.Length > MaxFileBytes) return false;

        // Документ только читается и отбрасывается; на диск он никогда не попадает.
        // Ограничиваем фактическое распакованное содержимое, а не только сведения ZIP.
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > entry.Length || output.Length + count > MaxFileBytes) return false;
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) return false;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(output.ToArray()); }
        catch (DecoderFallbackException) { return false; }
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f'))) return false;
        // Даже файл с именем README не принимаем за документацию, если это манифест ESO.
        return !text.TrimStart('\uFEFF').Split(['\r', '\n']).Any(line => line.TrimStart().StartsWith("##", StringComparison.Ordinal)
            && line.Contains(':') && new[] { "Title", "Author", "Version", "APIVersion", "AddOnVersion", "DependsOn", "OptionalDependsOn" }
                .Contains(line.TrimStart()[2..].Split(':', 2)[0].Trim(), StringComparer.OrdinalIgnoreCase));
    }
}
