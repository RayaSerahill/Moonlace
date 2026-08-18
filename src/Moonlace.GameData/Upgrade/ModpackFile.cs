using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumina.Data;
using Lumina.Data.Structs;

namespace Moonlace.GameData.Upgrade;

public sealed class ModpackException(string message) : Exception(message);

/// <summary>
/// Reads and writes distributable modpacks for the Dawntrail upgrade:
/// Penumbra .pmp files (a zip of the mod-folder layout) and .ttmp/.ttmp2
/// files (TTMPL.mpl manifest + TTMPD.mpd, whose entries are
/// SqPack-compressed blobs that Lumina's SqPackStream decompresses). Both
/// extract into a plain Penumbra mod folder the upgrader can work on, which
/// is then repackaged as a .pmp.
/// </summary>
public static class ModpackFile
{
    public static readonly string[] PickerPatterns = ["*.pmp", "*.ttmp2", "*.ttmp"];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The mod's display name, read cheaply without extracting.</summary>
    public static string PeekName(string modpackPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(modpackPath);
            var json = zip.GetEntry("meta.json") ?? zip.GetEntry("TTMPL.mpl");
            if (json is not null)
            {
                using var reader = new StreamReader(json.Open());
                if (JsonNode.Parse(reader.ReadToEnd()) is JsonObject root
                    && (string?)root["Name"] is { Length: > 0 } name)
                    return name;
            }
        }
        catch (Exception)
        {
            // Fall through to the file name; a real parse failure surfaces on extract.
        }

        return Path.GetFileNameWithoutExtension(modpackPath);
    }

    /// <summary>Extracts a modpack into a Penumbra mod-folder layout at <paramref name="targetDir"/>.</summary>
    public static void ExtractToFolder(string modpackPath, string targetDir, List<string> warnings)
    {
        Directory.CreateDirectory(targetDir);
        switch (Path.GetExtension(modpackPath).ToLowerInvariant())
        {
            case ".pmp":
                ZipFile.ExtractToDirectory(modpackPath, targetDir, overwriteFiles: true);
                if (!File.Exists(Path.Combine(targetDir, "meta.json")))
                    throw new ModpackException("This .pmp contains no meta.json — it is not a Penumbra mod package.");
                break;

            case ".ttmp" or ".ttmp2":
                ExtractTtmp(modpackPath, targetDir, warnings);
                break;

            default:
                throw new ModpackException("Select a .pmp, .ttmp2 or .ttmp modpack.");
        }
    }

    /// <summary>Packages a mod folder as a .pmp (atomically; Moonlace's backup store is not included).</summary>
    public static void PackagePmp(string modDirectory, string outputPath)
    {
        var root = Path.GetFullPath(modDirectory);
        var tmpPath = outputPath + ".tmp";
        try
        {
            using (var stream = File.Create(tmpPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (rel.StartsWith(Moonlace.Core.Penumbra.ModBackups.DirName + "/", StringComparison.Ordinal))
                        continue;
                    zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                }
            }

            File.Move(tmpPath, outputPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    // --- .ttmp modpacks ---

    private sealed class TtmpEntry
    {
        public string? Name { get; set; }

        public string? FullPath { get; set; }

        public long ModOffset { get; set; }

        public long ModSize { get; set; }
    }

    private sealed class TtmpOption
    {
        public string? Name { get; set; }

        public List<TtmpEntry>? ModsJsons { get; set; }

        public bool IsChecked { get; set; }
    }

    private sealed class TtmpGroup
    {
        public string? GroupName { get; set; }

        public string? SelectionType { get; set; }

        public List<TtmpOption>? OptionList { get; set; }
    }

    private sealed class TtmpPage
    {
        public List<TtmpGroup>? ModGroups { get; set; }
    }

    private sealed class TtmpManifest
    {
        public string? Name { get; set; }

        public string? Author { get; set; }

        public string? Version { get; set; }

        public string? Description { get; set; }

        public List<TtmpEntry>? SimpleModsList { get; set; }

        public List<TtmpPage>? ModPackPages { get; set; }
    }

    private static void ExtractTtmp(string modpackPath, string targetDir, List<string> warnings)
    {
        using var zip = ZipFile.OpenRead(modpackPath);
        var mplEntry = zip.GetEntry("TTMPL.mpl")
            ?? throw new ModpackException("TTMPL.mpl is missing — this is not a valid .ttmp modpack.");
        var mpdEntry = zip.GetEntry("TTMPD.mpd")
            ?? throw new ModpackException("TTMPD.mpd is missing — this is not a valid .ttmp modpack.");

        TtmpManifest manifest;
        using (var reader = new StreamReader(mplEntry.Open()))
        {
            var text = reader.ReadToEnd();
            manifest = ParseManifest(text);
        }

        // SqPackStream needs a seekable stream; pull the data blob out first.
        var mpdPath = Path.Combine(targetDir, "TTMPD.mpd.extract");
        mpdEntry.ExtractToFile(mpdPath, overwrite: true);
        try
        {
            using var mpdStream = File.OpenRead(mpdPath);
            using var sqpack = new SqPackStream(mpdStream, PlatformId.Win32);
            var writtenByOffset = new Dictionary<long, string>();
            var metaSkipped = 0;

            Dictionary<string, string> WriteEntries(IEnumerable<TtmpEntry> entries, string prefix)
            {
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    var gamePath = entry.FullPath?.Trim().Replace('\\', '/');
                    if (string.IsNullOrEmpty(gamePath))
                        continue;
                    if (gamePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                        || gamePath.EndsWith(".rgsp", StringComparison.OrdinalIgnoreCase))
                    {
                        // Metadata blobs (IMC/EQDP/EST…); Penumbra
                        // represents these as manipulations, which Moonlace
                        // does not translate.
                        metaSkipped++;
                        continue;
                    }

                    if (!writtenByOffset.TryGetValue(entry.ModOffset, out var rel))
                    {
                        rel = prefix.Length > 0 ? $"{prefix}/{gamePath}" : gamePath;
                        var target = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        byte[] data;
                        try
                        {
                            data = sqpack.ReadFile<FileResource>(entry.ModOffset).Data;
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not extract {gamePath}: {ex.Message}");
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        writtenByOffset[entry.ModOffset] = rel;
                    }

                    files[gamePath] = rel;
                }

                return files;
            }

            var defaultFiles = WriteEntries(manifest.SimpleModsList ?? [], "");
            WriteJson(Path.Combine(targetDir, "default_mod.json"), new JsonObject
            {
                ["Name"] = "",
                ["Priority"] = 0,
                ["Files"] = ToFilesNode(defaultFiles),
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            });

            var groupIndex = 0;
            foreach (var group in (manifest.ModPackPages ?? []).SelectMany(p => p.ModGroups ?? []))
            {
                groupIndex++;
                var groupName = string.IsNullOrWhiteSpace(group.GroupName) ? $"Group {groupIndex}" : group.GroupName!;
                var isMulti = string.Equals(group.SelectionType, "Multi", StringComparison.OrdinalIgnoreCase);
                var options = group.OptionList ?? [];

                long defaultSettings = 0;
                var optionNodes = new JsonArray();
                for (var i = 0; i < options.Count; i++)
                {
                    var option = options[i];
                    var optionName = string.IsNullOrWhiteSpace(option.Name) ? $"Option {i + 1}" : option.Name!;
                    var files = WriteEntries(option.ModsJsons ?? [],
                        $"{SafeSegment(groupName)}/{SafeSegment(optionName)}");
                    if (option.IsChecked)
                        defaultSettings = isMulti ? defaultSettings | (1L << i) : i;

                    optionNodes.Add(new JsonObject
                    {
                        ["Name"] = optionName,
                        ["Description"] = "",
                        ["Files"] = ToFilesNode(files),
                        ["FileSwaps"] = new JsonObject(),
                        ["Manipulations"] = new JsonArray(),
                    });
                }

                WriteJson(Path.Combine(targetDir, $"group_{groupIndex:D3}_{SafeSegment(groupName)}.json"), new JsonObject
                {
                    ["Name"] = groupName,
                    ["Description"] = "",
                    ["Priority"] = groupIndex,
                    ["Type"] = isMulti ? "Multi" : "Single",
                    ["DefaultSettings"] = defaultSettings,
                    ["Options"] = optionNodes,
                });
            }

            WriteJson(Path.Combine(targetDir, "meta.json"), new JsonObject
            {
                ["FileVersion"] = 3,
                ["Name"] = string.IsNullOrWhiteSpace(manifest.Name)
                    ? Path.GetFileNameWithoutExtension(modpackPath)
                    : manifest.Name,
                ["Author"] = manifest.Author ?? "",
                ["Description"] = manifest.Description ?? "",
                ["Version"] = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version,
                ["Website"] = "",
            });

            if (metaSkipped > 0)
                warnings.Add($"{metaSkipped} metadata entr{(metaSkipped == 1 ? "y was" : "ies were")} " +
                    "skipped (.meta/.rgsp) — IMC/EQDP-style edits are not carried into the PMP; " +
                    "import the original with Penumbra if those matter.");
        }
        finally
        {
            File.Delete(mpdPath);
        }
    }

    /// <summary>Parses TTMPL.mpl: a JSON object (v2), or the v1 one-entry-per-line format.</summary>
    private static TtmpManifest ParseManifest(string text)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<TtmpManifest>(text, ReadOptions);
            if (manifest is not null && (manifest.SimpleModsList is not null || manifest.ModPackPages is not null))
                return manifest;
        }
        catch (JsonException)
        {
        }

        var entries = new List<TtmpEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize<TtmpEntry>(line, ReadOptions) is { FullPath: not null } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }

        if (entries.Count == 0)
            throw new ModpackException("TTMPL.mpl could not be parsed — unsupported .ttmp modpack format.");
        return new TtmpManifest { SimpleModsList = entries };
    }

    private static JsonObject ToFilesNode(Dictionary<string, string> files)
    {
        var node = new JsonObject();
        foreach (var (gamePath, rel) in files)
            node[gamePath] = rel.Replace('/', '\\');
        return node;
    }

    private static void WriteJson(string path, JsonObject root) =>
        File.WriteAllText(path, root.ToJsonString(WriteOptions));

    private static string SafeSegment(string name)
    {
        var safe = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' ? c : '_')
            .ToArray());
        return safe.Trim('_').Length == 0 ? "x" : safe;
    }
}
