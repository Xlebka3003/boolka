using System.IO;
using System.Text.Json;

namespace Boolka;

public sealed record FolderSettings(string? EsoAddonFolder);
public sealed record SettingsLoadResult(string? Folder, string? Warning = null);

public sealed class SettingsStore
{
    private readonly string filePath;
    private readonly bool defaultLocation;
    private bool prepared;
    private string? migrationWarning;
    public string PreferencesPath { get { PrepareStorage(); return Path.ChangeExtension(filePath, "preferences.json"); } }

    private void PrepareStorage()
    {
        if (prepared || !defaultLocation) return;
        prepared = true;
        try { StorageMigration.SettingsFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Error("ui.operation.failed", ex);
            migrationWarning = "Не удалось перенести прежние настройки в boolka: " + ex.Message;
        }
    }

    public SettingsStore(string? filePath = null)
    {
        defaultLocation = filePath is null;
        this.filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "boolka", "settings.json");
    }

    public static string GetDefaultAddonFolder(string? documentsFolder = null)
    {
        // Windows возвращает расположение Документов текущего пользователя, включая перенос в OneDrive.
        var documents = documentsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "Elder Scrolls Online", "live", "AddOns");
    }

    public SettingsLoadResult Load()
    {
        PrepareStorage();
        try
        {
            if (!File.Exists(filePath)) return new(null, migrationWarning);
            if (new FileInfo(filePath).Length > 64 * 1024) return new(null, "Файл настроек повреждён. Выберите папку заново.");
            var settings = JsonSerializer.Deserialize<FolderSettings>(File.ReadAllText(filePath));
            var folder = settings?.EsoAddonFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                return new(null, "В настройках нет корректного пути. Выберите папку аддонов.");
            return new(Path.GetFullPath(folder), migrationWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("ui.operation.failed", ex);
            return new(null, "Не удалось прочитать настройки. Выберите папку аддонов заново.");
        }
    }

    public void Save(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Укажите полный путь к папке аддонов.", nameof(folder));
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new FolderSettings(Path.GetFullPath(folder)), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
