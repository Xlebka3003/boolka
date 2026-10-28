using System.Windows;

namespace Boolka;

public static class UiResources
{
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

    public static void Apply(string language)
    {
        UiText.Language = language;
        if (Application.Current is not { } app) return;
        foreach (var original in UiText.English.Keys.Concat(UiResourceStrings.All).Concat(Registered))
            app.Resources[UiText.ResourceKey(original)] = UiText.Get(original);
    }

    public static void Register(string original)
    {
        Registered.Add(original);
        if (Application.Current is { } app)
            app.Resources[UiText.ResourceKey(original)] = UiText.Get(original);
    }
}
