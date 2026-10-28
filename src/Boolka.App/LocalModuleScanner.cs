using System.IO;

namespace Boolka;

public sealed record LocalModuleScan(IReadOnlyList<CatalogModule> Modules, bool Complete);

/// <summary>Discovery only: a nested manifest does not prove the game loads that module.</summary>
public static class LocalModuleScanner
{
    public static LocalModuleScan Scan(string root, CatalogModule rootModule)
    {
        var modules = new List<CatalogModule> { rootModule }; var complete = true; var directories = 0;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (parent, depth) = pending.Pop();
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                foreach (var directory in Directory.EnumerateDirectories(parent))
                {
                    if (++directories > 1000) return new(modules, false);
                    var name = Path.GetFileName(directory);
                    if (name.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                    if (depth >= 8) { complete = false; continue; }
                    pending.Push((directory, depth + 1));
                    var manifest = new[] { Path.Combine(directory, name + ".txt"), Path.Combine(directory, name + ".addon") }.FirstOrDefault(File.Exists);
                    if (manifest is null) continue;
                    if (modules.Count >= 256) return new(modules, false);
                    if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifest).Length > 1024 * 1024) { complete = false; continue; }
                    var fields = File.ReadLines(manifest).Where(x => x.StartsWith("##") && x.Contains(':')).Select(x => x[2..].Split(':', 2))
                        .GroupBy(x => x[0].Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Key.Equals("DependsOn", StringComparison.OrdinalIgnoreCase) ? string.Join(" ", x.Select(y => y[1].Trim())) : x.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var relative = rootModule.Path + "/" + Path.GetRelativePath(root, directory).Replace('\\', '/');
                    modules.Add(new(relative, fields.GetValueOrDefault("AddOnVersion", ""), fields.GetValueOrDefault("DependsOn", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), true));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        }
        return new(modules, complete);
    }
}
