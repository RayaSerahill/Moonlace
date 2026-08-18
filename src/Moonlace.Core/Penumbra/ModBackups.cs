using System.Text.Json;

namespace Moonlace.Core.Penumbra;

/// <summary>One backed-up mod file: its mod-relative path and whether it existed before the first edit.</summary>
public sealed class ModBackupEntry
{
    public string RelativePath { get; set; } = "";

    public bool Existed { get; set; }
}

/// <summary>
/// The shared on-disk backup store inside a Penumbra mod folder
/// (.moonlace-backup/manifest.json + files/…). Both the live-edit link and
/// the Dawntrail upgrader write through this, so one Revert covers
/// everything and a later relink picks earlier backups up.
/// </summary>
public static class ModBackups
{
    public const string DirName = ".moonlace-backup";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class Manifest
    {
        public List<ModBackupEntry> Entries { get; set; } = [];
    }

    public static string ManifestPath(string modDirectory) =>
        Path.Combine(modDirectory, DirName, "manifest.json");

    public static string BackupFilePath(string modDirectory, string relativePath) =>
        Path.Combine(modDirectory, DirName, "files", relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Loads the manifest, or an empty list when there is none (or it is unreadable).</summary>
    public static List<ModBackupEntry> Load(string modDirectory)
    {
        try
        {
            var path = ManifestPath(modDirectory);
            if (File.Exists(path))
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null)
                    return manifest.Entries;
            }
        }
        catch (Exception)
        {
            // A corrupt manifest must not block editing; a fresh one starts empty.
        }

        return [];
    }

    public static void Save(string modDirectory, List<ModBackupEntry> entries)
    {
        var dir = Path.Combine(modDirectory, DirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(new Manifest { Entries = entries }, JsonOptions));
    }

    /// <summary>
    /// Ensures a mod file is backed up before its first modification: copies
    /// the original (when it exists), records the entry and saves the
    /// manifest. Later calls for the same path are no-ops, preserving the
    /// pre-edit state. Returns true when a new entry was recorded.
    /// </summary>
    public static bool EnsureBackedUp(
        string modDirectory, List<ModBackupEntry> entries, string relativePath, string targetPath)
    {
        if (entries.Any(e => string.Equals(e.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
            return false;

        var existed = File.Exists(targetPath);
        if (existed)
        {
            var backup = BackupFilePath(modDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(targetPath, backup, overwrite: false);
        }

        entries.Add(new ModBackupEntry { RelativePath = relativePath, Existed = existed });
        Save(modDirectory, entries);
        return true;
    }
}
