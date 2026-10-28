using System.IO;
using System.Text.Json;

namespace Boolka;

public sealed record AppPreferences(bool CompactRows = true, int CatalogFilter = 0, long[]? IgnoredIds = null,
    string Language = "en", string Theme = "dracula", bool? SquareCorners = true, long CategoryId = 0, bool ReverseSort = false, int SortIndex = 1, long InstalledCategoryId = 0, string PreviewMode = "icons", double CollectionScale = 1, bool? DescendingSort = null, int TileColumns = 4, double DescriptionScale = 1, bool LaunchConfigured = false, bool LaunchSteam = true, string GameExecutable = "", bool LaunchTtc = true, bool LaunchHarvest = true, string HarvestPreparationStamp = "", LaunchProgram[]? LaunchPrograms = null, bool AutoUpdateAddonsOnStartup = false);

public sealed class PreferencesStore(string path)
{
    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return new();
            var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new();
            return value with
            {
                LaunchPrograms = value.LaunchPrograms is null ? null : LaunchProgram.Normalize(value.LaunchPrograms),
                GameExecutable = value.GameExecutable ?? "",
                HarvestPreparationStamp = value.HarvestPreparationStamp ?? "",
                CatalogFilter = Math.Clamp(value.CatalogFilter, 0, 2),
                CategoryId = Math.Max(0, value.CategoryId),
                InstalledCategoryId = Math.Max(-1, value.InstalledCategoryId),
                PreviewMode = value.PreviewMode == "images" ? "images" : "icons",
                CollectionScale = double.IsFinite(value.CollectionScale) ? Math.Clamp(value.CollectionScale, 0.5, 1.5) : 1,
                SortIndex = Math.Clamp(value.SortIndex, 0, 2),
                TileColumns = Math.Clamp(value.TileColumns, 1, 8),
                // Сохраняем прежний фактический порядок выбранного поля при переходе на единое направление.
                DescendingSort = value.DescendingSort ?? ((Math.Clamp(value.SortIndex, 0, 2) != 0) != value.ReverseSort),
                Language = UiLanguages.Normalize(value.Language),
                DescriptionScale = double.IsFinite(value.DescriptionScale) ? Math.Clamp(value.DescriptionScale, 0.5, 1.5) : 1,
                Theme = value.Theme is "light" or "classic" or "warm" ? "warm"
                    : value.Theme is "graphite" or "dark" or "classic-dark" ? "graphite" : "dracula",
                SquareCorners = true,
                IgnoredIds = (value.IgnoredIds ?? []).Where(x => x > 0).Distinct().Take(4096).ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    public void Save(AppPreferences value)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
