using System.IO.Compression;
using System.Text.Json;
using Moonlace.Core.Session;

namespace Moonlace.GameData.Export;

public sealed class PmpMetadata
{
    public string Name { get; set; } = "Moonlace Export";

    public string Author { get; set; } = "";

    public string Version { get; set; } = "1.0.0";

    public string Description { get; set; } = "";
}

public sealed class PmpExportException(string message) : Exception(message);

/// <summary>
/// Packages the active session's modified assets as a Penumbra Mod Package
/// (.pmp): a zip containing meta.json, default_mod.json and the modified
/// files, keyed by their logical game paths. Exports strictly from the
/// session manifest — the live game files are never consulted for content,
/// and only changed assets are included.
/// </summary>
public static class PmpExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Export(
        ISessionService session,
        PmpMetadata metadata,
        string outputPath)
    {
        var entries = session.Entries;
        if (entries.Count == 0)
            throw new PmpExportException("The current session has no changes — there is nothing to export.");
        if (string.IsNullOrWhiteSpace(metadata.Name))
            throw new PmpExportException("The mod needs a name.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var bytes = session.TryReadAsset(entry.GamePath)
                ?? throw new PmpExportException(
                    $"Session data for \"{entry.GamePath}\" is missing. Discard the session and redo the change.");
            files[entry.GamePath] = bytes;
        }

        var meta = new Dictionary<string, object>
        {
            ["FileVersion"] = 3,
            ["Name"] = metadata.Name.Trim(),
            ["Author"] = metadata.Author.Trim(),
            ["Description"] = metadata.Description.Trim(),
            ["Version"] = string.IsNullOrWhiteSpace(metadata.Version) ? "1.0.0" : metadata.Version.Trim(),
            ["Website"] = "",
        };

        var fileMap = files.Keys.ToDictionary(
            gamePath => gamePath,
            gamePath => "files/" + gamePath.Replace('/', '_'),
            StringComparer.Ordinal);

        var defaultMod = new Dictionary<string, object>
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = fileMap,
            ["FileSwaps"] = new Dictionary<string, string>(),
            ["Manipulations"] = Array.Empty<object>(),
        };

        // Write to a temp file first so a failed export never leaves a broken .pmp.
        var tmpPath = outputPath + ".tmp";
        try
        {
            using (var zipStream = File.Create(tmpPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteJsonEntry(zip, "meta.json", meta);
                WriteJsonEntry(zip, "default_mod.json", defaultMod);
                foreach (var (gamePath, bytes) in files)
                {
                    var entry = zip.CreateEntry(fileMap[gamePath], CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(bytes);
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

    private static void WriteJsonEntry(ZipArchive zip, string name, object payload)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
