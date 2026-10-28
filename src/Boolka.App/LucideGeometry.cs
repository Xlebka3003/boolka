using System.Windows.Media;

namespace Boolka;

// Геометрия преобразована из сохранённых оригиналов Lucide SVG; лицензия лежит в Assets/Lucide.
internal static class LucideGeometry
{
    private static Geometry Parse(string path) { var geometry = Geometry.Parse(path); geometry.Freeze(); return geometry; }
    private static readonly IReadOnlyDictionary<string, Geometry> Icons = new Dictionary<string, Geometry>
    {
        ["arrow-down"] = Parse("M 12 5 v 14 M 19 12 l -7 7 -7 -7"),
        ["arrow-left"] = Parse("m 12 19 -7 -7 7 -7 M 19 12 H 5"),
        ["arrow-right"] = Parse("M 5 12 h 14 M 12 5 l 7 7 -7 7"),
        ["arrow-up"] = Parse("m 5 12 7 -7 7 7 M 12 19 V 5"),
        ["chevron-left"] = Parse("m 15 18 -6 -6 6 -6"),
        ["chevron-right"] = Parse("m 9 18 6 -6 -6 -6"),
        ["copy"] = Parse("M 10.0 8.0 H 20.0 Q 22.0 8.0 22.0 10.0 V 20.0 Q 22.0 22.0 20.0 22.0 H 10.0 Q 8.0 22.0 8.0 20.0 V 10.0 Q 8.0 8.0 10.0 8.0 Z M 4 16 c -1.1 0 -2 -.9 -2 -2 V 4 c 0 -1.1 .9 -2 2 -2 h 10 c 1.1 0 2 .9 2 2"),
        ["external-link"] = Parse("M 15 3 h 6 v 6 M 10 14 21 3 M 18 13 v 6 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 8 a 2 2 0 0 1 2 -2 h 6"),
        ["heart"] = Parse("M 2 9.5 a 5.5 5.5 0 0 1 9.591 -3.676 .56 .56 0 0 0 .818 0 A 5.49 5.49 0 0 1 22 9.5 c 0 2.29 -1.5 4 -3 5.5 l -5.492 5.313 a 2 2 0 0 1 -3 .019 L 5 15 c -1.5 -1.5 -3 -3.2 -3 -5.5"),
        ["refresh-cw"] = Parse("M 3 12 a 9 9 0 0 1 9 -9 9.75 9.75 0 0 1 6.74 2.74 L 21 8 M 21 3 v 5 h -5 M 21 12 a 9 9 0 0 1 -9 9 9.75 9.75 0 0 1 -6.74 -2.74 L 3 16 M 8 16 H 3 v 5"),
        ["search"] = Parse("M 21 21 l -4.34 -4.34 M 19 11 A 8 8 0 1 0 3 11 A 8 8 0 1 0 19 11"),
        ["play"] = Parse("M 6 3 L 20 12 L 6 21 Z"),
        ["minus"] = Parse("M 5 12 h 14"),
        ["plus"] = Parse("M 5 12 h 14 M 12 5 v 14"),
        ["settings"] = Parse("M 9.671 4.136 a 2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1 -2.33 4.033 2.34 2.34 0 0 0 -3.319 1.915 2.34 2.34 0 0 1 -4.659 0 2.34 2.34 0 0 0 -3.32 -1.915 2.34 2.34 0 0 1 -2.33 -4.033 2.34 2.34 0 0 0 0 -3.831 A 2.34 2.34 0 0 1 6.35 6.051 a 2.34 2.34 0 0 0 3.319 -1.915 M 9.0 12.0 A 3.0 3.0 0 1 0 15.0 12.0 A 3.0 3.0 0 1 0 9.0 12.0"),
        ["square"] = Parse("M 5.0 3.0 H 19.0 Q 21.0 3.0 21.0 5.0 V 19.0 Q 21.0 21.0 19.0 21.0 H 5.0 Q 3.0 21.0 3.0 19.0 V 5.0 Q 3.0 3.0 5.0 3.0 Z"),
        ["x"] = Parse("M 18 6 6 18 M 6 6 l 12 12"),
    };
    public static Geometry Get(string name) => Icons.TryGetValue(name, out var icon) ? icon : Icons["square"];
}
