namespace Boolka;

internal static class ManifestFields
{
    // В Windows общие и PC-зависимости обязательны одновременно; ConsoleDependsOn не применяется.
    internal static string Normalize(string field) => field.Trim().Equals("PCDependsOn", StringComparison.OrdinalIgnoreCase)
        ? "DependsOn" : field.Trim();
}
