using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Penumbra;
using Moonlace.Core.Session;
using Moonlace.GameData.Upgrade;

namespace Moonlace.GameData.Import;

public sealed class ModpackImportException(string message) : Exception(message);

public sealed class ModpackImportReport
{
    public string ModName = "";

    /// <summary>Where the files landed, phrased for the UI ("the session for “X”" / "the linked mod “Y”").</summary>
    public string Destination = "";

    public int FilesImported;

    public int PseudoPathsSkipped;

    public List<string> Warnings = [];

    public string Summary()
    {
        if (FilesImported == 0)
            return "Nothing was imported — the modpack contains no usable file redirections.";

        var parts = new List<string>
        {
            $"{FilesImported} file{(FilesImported == 1 ? "" : "s")} imported into {Destination}",
        };
        if (PseudoPathsSkipped > 0)
            parts.Add($"{PseudoPathsSkipped} unused file{(PseudoPathsSkipped == 1 ? "" : "s")} skipped");
        return string.Join(" · ", parts) + ".";
    }
}

/// <summary>
/// Imports a distributable modpack (.pmp / .ttmp2 / .ttmp) as edits: every
/// file the mod redirects with its default option selection is written
/// through the same path user edits take — into the active item's session, or
/// straight into the linked Penumbra mod while a live-edit link is active
/// (backed up and revertible like any other live edit). The modpack file
/// itself and the FFXIV installation are never modified.
/// </summary>
public sealed class ModpackImporter
{
    private readonly ISessionService _session;
    private readonly IPenumbraLinkService _link;
    private readonly ILogger<ModpackImporter> _logger;

    public ModpackImporter(ISessionService session, IPenumbraLinkService link, ILogger<ModpackImporter> logger)
    {
        _session = session;
        _link = link;
        _logger = logger;
    }

    /// <summary>Where an import would land right now, phrased for the UI; null when no destination exists yet.</summary>
    public string? DescribeDestination()
    {
        if (_link.IsLinked)
        {
            return _link.EditTarget is { } target
                ? $"option “{target.Group} / {target.Option}” of the linked mod “{_link.ModName}”"
                : $"the linked mod “{_link.ModName}”";
        }

        return _session.ActiveItem is { } item ? $"the session for “{item.Name}”" : null;
    }

    public Task<ModpackImportReport> ImportAsync(string modpackPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var destination = DescribeDestination()
                ?? throw new ModpackImportException(
                    "Select an item first — without a Penumbra link, imported files become that item's session edits.");

            var tempDir = Path.Combine(Path.GetTempPath(), "moonlace-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                var report = new ModpackImportReport { Destination = destination };
                ModpackFile.ExtractToFolder(modpackPath, tempDir, report.Warnings);
                var info = _link.Inspect(tempDir);
                report.ModName = info.Name;

                var root = Path.GetFullPath(info.Directory);
                foreach (var (gamePath, rel) in EffectiveFiles(info))
                {
                    ct.ThrowIfCancellationRequested();

                    // Penumbra keeps a mod's unused files under pseudo-keys
                    // ("Option Name/chara/…") the game never requests.
                    if (!ModPaths.LooksLikeGamePath(gamePath))
                    {
                        report.PseudoPathsSkipped++;
                        continue;
                    }

                    var file = Path.GetFullPath(ModPaths.ResolveCaseInsensitive(root, rel));
                    if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        report.Warnings.Add($"\"{rel}\" points outside the modpack — skipped.");
                        continue;
                    }

                    if (!File.Exists(file))
                    {
                        report.Warnings.Add($"Missing modpack file for {gamePath} ({rel}).");
                        continue;
                    }

                    var data = File.ReadAllBytes(file);
                    if (_link.IsLinked)
                        _link.WriteAsset(gamePath, data);
                    else
                        _session.StoreAsset(gamePath, KindOf(gamePath), data);
                    report.FilesImported++;
                }

                WarnAboutUnimportedOptions(info, report);
                WarnAboutManipulations(root, report);
                _logger.LogInformation("Imported modpack \"{Mod}\": {Summary}", info.Name, report.Summary());
                return report;
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
                    _logger.LogWarning(ex, "Could not remove the temporary import folder {Dir}", tempDir);
                }
            }
        }, ct);
    }

    /// <summary>
    /// The modpack's effective game-path → mod-relative-path map for its
    /// default option selection, layered in Penumbra precedence (group
    /// priority, then option priority for Multi groups, later layers win).
    /// </summary>
    private static Dictionary<string, string> EffectiveFiles(PenumbraModInfo info)
    {
        var map = new Dictionary<string, string>(info.DefaultFiles, StringComparer.OrdinalIgnoreCase);

        var layers = new List<(int GroupPriority, int OptionPriority, int GroupIndex, int OptionIndex, PenumbraOption Option)>();
        for (var gi = 0; gi < info.Groups.Count; gi++)
        {
            var group = info.Groups[gi];
            foreach (var oi in group.DefaultSelection())
            {
                var option = group.Options[oi];
                var optionPriority = group.Type == PenumbraGroupType.Multi ? option.Priority : 0;
                layers.Add((group.Priority, optionPriority, gi, oi, option));
            }
        }

        foreach (var layer in layers
                     .OrderBy(l => l.GroupPriority)
                     .ThenBy(l => l.OptionPriority)
                     .ThenBy(l => l.GroupIndex)
                     .ThenBy(l => l.OptionIndex))
        {
            foreach (var (gamePath, rel) in layer.Option.Files)
                map[gamePath] = rel;
        }

        return map;
    }

    private static void WarnAboutUnimportedOptions(PenumbraModInfo info, ModpackImportReport report)
    {
        var unselected = info.Groups
            .Sum(g => g.Options.Count(o => o.Files.Count > 0) - g.DefaultSelection().Count(i => g.Options[i].Files.Count > 0));
        if (unselected > 0)
            report.Warnings.Add(
                $"The mod's default option selection was imported; {unselected} other " +
                $"option{(unselected == 1 ? "" : "s")} with files of their own were left out.");
    }

    /// <summary>Counts Manipulations entries across the extracted mod's JSON files (both layouts) and warns when any exist.</summary>
    private static void WarnAboutManipulations(string root, ModpackImportReport report)
    {
        var count = 0;
        foreach (var jsonFile in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                count += CountManipulations(JsonNode.Parse(File.ReadAllText(jsonFile)));
            }
            catch (JsonException)
            {
                // Unrelated or malformed JSON in the pack — the extract already validated what matters.
            }
        }

        if (count > 0)
            report.Warnings.Add(
                $"{count} metadata manipulation{(count == 1 ? " was" : "s were")} not imported (IMC/EQP/EST…) — " +
                "Moonlace edits files only; install the original mod with Penumbra if those matter.");
    }

    private static int CountManipulations(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(kv =>
            kv.Key == "Manipulations" && kv.Value is JsonArray manips ? manips.Count : CountManipulations(kv.Value)),
        JsonArray array => array.Sum(CountManipulations),
        _ => 0,
    };

    private static SessionAssetKind KindOf(string gamePath) => Path.GetExtension(gamePath).ToLowerInvariant() switch
    {
        ".mdl" => SessionAssetKind.Model,
        ".mtrl" => SessionAssetKind.Material,
        ".tex" => SessionAssetKind.Texture,
        _ => SessionAssetKind.Other,
    };
}
