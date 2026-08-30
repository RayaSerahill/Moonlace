using Microsoft.Extensions.Logging;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.Import;
using Moonlace.GameData.Resolution;
using Moonlace.GameData.Upgrade;

namespace Moonlace.GameData.ModTools;

/// <summary>
/// Shared plumbing for the mod tools: extracts a modpack (.pmp / .ttmp2 /
/// .ttmp) into a temporary folder, computes its effective file map for the
/// default option selection, runs the tool's work on it and cleans up
/// afterwards. The input modpack is never modified.
/// </summary>
internal static class ModpackExtraction
{
    internal static T RunExtracted<T>(
        IPenumbraLinkService link,
        ILogger logger,
        string modpackPath,
        List<string> warnings,
        Func<PenumbraModInfo, IReadOnlyDictionary<string, string>, string, T> work)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "moonlace-retarget-" + Guid.NewGuid().ToString("N"));
        try
        {
            ModpackFile.ExtractToFolder(modpackPath, tempDir, warnings);
            var info = link.Inspect(tempDir);
            var effective = ModpackImporter.EffectiveFiles(info);
            return work(info, effective, Path.GetFullPath(info.Directory));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the temporary retarget folder {Dir}", tempDir);
            }
        }
    }

    /// <summary>Reads one of the extracted modpack's files, guarding against paths escaping the folder.</summary>
    internal static byte[]? ReadModFile(string root, string rel, string gamePath, List<string> warnings)
    {
        var file = Path.GetFullPath(ModPaths.ResolveCaseInsensitive(root, rel));
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            warnings.Add($"\"{rel}\" points outside the modpack: skipped.");
            return null;
        }

        if (!File.Exists(file))
        {
            warnings.Add($"Missing modpack file for {gamePath} ({rel}).");
            return null;
        }

        return File.ReadAllBytes(file);
    }
}
