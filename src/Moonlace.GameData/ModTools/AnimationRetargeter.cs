using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.Export;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.ModTools;

public sealed class AnimationRetargetException(string message) : Exception(message);

/// <summary>
/// One retargetable animation found in a modpack: every file (race paps and
/// the optional timeline) that belongs to one ActionTimeline key.
/// </summary>
public sealed class AnimationBinding
{
    /// <summary>The ActionTimeline key, e.g. "emote/dance".</summary>
    public required string TimelineKey { get; init; }

    /// <summary>What plays this timeline: emote name(s) or a friendly pose name, the key itself as fallback.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The binding's game paths (lowercased), sorted.</summary>
    public required IReadOnlyList<string> GamePaths { get; init; }

    /// <summary>Race codes of the .pap files, e.g. ["0101", "0201"].</summary>
    public required IReadOnlyList<string> RaceCodes { get; init; }

    public bool HasTimelineFile => GamePaths.Any(p => p.EndsWith(".tmb", StringComparison.Ordinal));

    public string Label =>
        $"{DisplayName} · {TimelineKey} · {GamePaths.Count} file{(GamePaths.Count == 1 ? "" : "s")}";
}

/// <summary>One retarget decision: this animation's files move onto that timeline.</summary>
public sealed record AnimationRetargetAssignment(AnimationBinding Binding, AnimationTimeline Destination);

public sealed class AnimationRetargetAnalysis
{
    public required string ModName { get; init; }

    public required IReadOnlyList<AnimationBinding> Bindings { get; init; }

    /// <summary>Files that are not retargetable animation assets and would be carried unchanged.</summary>
    public required int CarriedFileCount { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed class AnimationRetargetReport
{
    public string ModName = "";

    public List<string> AssignmentLabels = [];

    public int FilesRewired;

    public int PapsRenamed;

    public int TimelinesDropped;

    public int FilesCarried;

    public List<string> Warnings = [];

    public string Summary()
    {
        var parts = new List<string>
        {
            $"{FilesRewired} file{(FilesRewired == 1 ? "" : "s")} rewired: {string.Join("; ", AssignmentLabels)}",
        };
        if (PapsRenamed > 0)
            parts.Add($"{PapsRenamed} animation container{(PapsRenamed == 1 ? "" : "s")} renamed to the destination's clip names");
        if (TimelinesDropped > 0)
            parts.Add($"{TimelinesDropped} unmodified timeline{(TimelinesDropped == 1 ? "" : "s")} dropped (the destination keeps its own)");
        if (FilesCarried > 0)
            parts.Add($"{FilesCarried} other file{(FilesCarried == 1 ? "" : "s")} carried unchanged");
        return string.Join(" · ", parts) + ".";
    }
}

/// <summary>
/// Retargets a distributable animation modpack (.pmp / .ttmp2 / .ttmp) onto
/// different emotes or animations, one assignment per modded timeline key.
/// The .pap and .tmb game paths are remapped onto the destination key, and
/// pap animation clip names are patched (fixed-width header fields) to the
/// names the destination's vanilla timeline references, so the motion
/// actually plays on the new emote. Timelines the mod ships unmodified are
/// dropped in favor of the destination's own. The output is a new standalone
/// .pmp; the input modpack and the FFXIV installation are never modified.
/// </summary>
public sealed partial class AnimationRetargeter
{
    private readonly LuminaGameDataService _gameData;
    private readonly AnimationTimelineCatalog _catalog;
    private readonly IPenumbraLinkService _link;
    private readonly ILogger<AnimationRetargeter> _logger;

    public AnimationRetargeter(
        LuminaGameDataService gameData,
        AnimationTimelineCatalog catalog,
        IPenumbraLinkService link,
        ILogger<AnimationRetargeter> logger)
    {
        _gameData = gameData;
        _catalog = catalog;
        _link = link;
        _logger = logger;
    }

    /// <summary>Which animation timelines the modpack's effective files (default option selection) affect.</summary>
    public Task<AnimationRetargetAnalysis> AnalyzeAsync(string modpackPath, CancellationToken ct = default)
        => Task.Run(() => Analyze(modpackPath, ct), ct);

    private AnimationRetargetAnalysis Analyze(string modpackPath, CancellationToken ct)
    {
        var notes = new List<string>();
        return ModpackExtraction.RunExtracted(_link, _logger, modpackPath, notes, (info, effective, root) =>
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var carried = 0;
            var unmatchedAnimations = 0;

            foreach (var rawPath in effective.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (!ModPaths.LooksLikeGamePath(rawPath))
                    continue; // Penumbra pseudo-key for an unused file, not effective content

                var path = rawPath.ToLowerInvariant();
                var key = _catalog.MatchKey(path);
                if (key is not null)
                {
                    if (!groups.TryGetValue(key, out var list))
                        groups[key] = list = [];
                    list.Add(path);
                    continue;
                }

                carried++;
                if (path.EndsWith(".pap", StringComparison.Ordinal) || path.EndsWith(".tmb", StringComparison.Ordinal))
                    unmatchedAnimations++;
            }

            var bindings = groups
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new AnimationBinding
                {
                    TimelineKey = g.Key,
                    DisplayName = _catalog.Describe(g.Key).DisplayName,
                    GamePaths = g.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    RaceCodes = g.Value
                        .Where(p => p.EndsWith(".pap", StringComparison.Ordinal))
                        .Select(p => RaceDirRegex().Match(p))
                        .Where(m => m.Success)
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray();

            if (unmatchedAnimations > 0)
                notes.Add($"{unmatchedAnimations} animation file{(unmatchedAnimations == 1 ? "" : "s")} did not match any known animation timeline and would be carried unchanged.");
            if (carried > unmatchedAnimations)
                notes.Add($"{carried - unmatchedAnimations} file{(carried - unmatchedAnimations == 1 ? " is" : "s are")} not animation assets (sounds, VFX, gear, ...) and would be carried unchanged.");

            _logger.LogInformation("Analyzed modpack \"{Mod}\": {Bindings} animation binding(s), {Carried} carried",
                info.Name, bindings.Length, carried);
            return new AnimationRetargetAnalysis
            {
                ModName = info.Name,
                Bindings = bindings,
                CarriedFileCount = carried,
                Notes = notes,
            };
        });
    }

    /// <summary>Convenience overload for a single assignment.</summary>
    public Task<AnimationRetargetReport> RetargetAsync(
        string modpackPath,
        AnimationBinding binding,
        AnimationTimeline destination,
        string outputPath,
        CancellationToken ct = default)
        => RetargetAsync(modpackPath, [new AnimationRetargetAssignment(binding, destination)], outputPath, ct);

    /// <summary>
    /// Rewires each assigned animation of the modpack onto its own destination
    /// timeline and writes the combined result as a new .pmp at
    /// <paramref name="outputPath"/>. Bindings without an assignment and all
    /// other files are carried through unchanged.
    /// </summary>
    public Task<AnimationRetargetReport> RetargetAsync(
        string modpackPath,
        IReadOnlyList<AnimationRetargetAssignment> assignments,
        string outputPath,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (assignments.Count == 0)
                throw new AnimationRetargetException("Assign at least one modded animation a new target.");
            if (assignments.Select(a => a.Binding).Distinct().Count() != assignments.Count)
                throw new AnimationRetargetException("Each modded animation can only have one new target.");
            foreach (var assignment in assignments)
            {
                if (assignment.Destination.Key == assignment.Binding.TimelineKey)
                    throw new AnimationRetargetException(
                        $"{assignment.Binding.DisplayName} already targets {assignment.Destination.Key}; nothing to change.");
            }

            var report = new AnimationRetargetReport();
            return ModpackExtraction.RunExtracted(_link, _logger, modpackPath, report.Warnings, (info, effective, root) =>
            {
                report.ModName = info.Name;

                var relByPath = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (rawPath, rel) in effective)
                    relByPath.TryAdd(rawPath.ToLowerInvariant(), rel);
                byte[]? ReadBinding(string path) => relByPath.TryGetValue(path, out var rel)
                    ? ModpackExtraction.ReadModFile(root, rel, path, report.Warnings)
                    : null;

                var plans = assignments.Select(a => BuildPlan(a, ReadBinding, report.Warnings)).ToArray();
                report.AssignmentLabels.AddRange(plans.Select(p => p.Label));
                var planByPath = new Dictionary<string, RetargetPlan>(StringComparer.Ordinal);
                foreach (var plan in plans)
                {
                    foreach (var path in plan.Binding.GamePaths)
                        planByPath[path] = plan;
                }

                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var (rawPath, rel) in effective.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ModPaths.LooksLikeGamePath(rawPath))
                        continue;

                    var path = rawPath.ToLowerInvariant();
                    var bytes = ModpackExtraction.ReadModFile(root, rel, path, report.Warnings);
                    if (bytes is null)
                        continue;

                    if (planByPath.TryGetValue(path, out var plan))
                    {
                        if (!RetargetFile(plan, path, bytes, files, report))
                            continue;
                    }
                    else
                    {
                        if (!files.TryAdd(path, bytes))
                            report.Warnings.Add($"A retargeted file replaces the mod's own {path}.");
                        report.FilesCarried++;
                    }
                }

                if (report.FilesRewired == 0)
                    throw new AnimationRetargetException("None of the mod's files matched the assigned animations; nothing to retarget.");

                var destinationNames = plans.Select(p => p.Assignment.Destination.DisplayName).Distinct().ToArray();
                PmpExporter.Export(files, new PmpMetadata
                {
                    Name = $"{info.Name} → {string.Join(" + ", destinationNames)}",
                    Description = $"Retargeted by Moonlace: {string.Join("; ", report.AssignmentLabels)}.",
                }, outputPath);

                _logger.LogInformation("Retargeted \"{Mod}\": {Summary} → {Out}", info.Name, report.Summary(), outputPath);
                return report;
            });
        }, ct);
    }

    /// <summary>Everything one assignment needs while files stream through.</summary>
    private sealed class RetargetPlan
    {
        public required AnimationRetargetAssignment Assignment { get; init; }

        public required string Label { get; init; }

        /// <summary>The mod ships an edited .tmb, so its paps keep their own clip names to stay consistent with it.</summary>
        public required bool HasCustomTimeline { get; init; }

        public bool WarnedCustomTimeline;

        public AnimationBinding Binding => Assignment.Binding;
    }

    private RetargetPlan BuildPlan(
        AnimationRetargetAssignment assignment, Func<string, byte[]?> readBinding, List<string> warnings)
    {
        var (binding, destination) = assignment;

        // An unmodified timeline is redundant after the retarget (the
        // destination's own vanilla .tmb takes over), while an edited one is
        // moved as-is and keeps referencing the mod's own clip names — the
        // paps then stay untouched so the pair remains consistent.
        var hasCustomTimeline = false;
        foreach (var path in binding.GamePaths.Where(p => p.EndsWith(".tmb", StringComparison.Ordinal)))
        {
            if (readBinding(path) is { } modBytes && !IsVanillaTimeline(path, modBytes))
                hasCustomTimeline = true;
        }

        if (hasCustomTimeline && !binding.GamePaths.Any(p => p.EndsWith(".pap", StringComparison.Ordinal)))
            warnings.Add(
                $"{binding.DisplayName}: the mod ships an edited timeline but no animation file; " +
                "the moved timeline still references the original animation clips and may not play correctly " +
                $"on {destination.DisplayName}.");

        return new RetargetPlan
        {
            Assignment = assignment,
            Label = $"{binding.DisplayName} ({binding.TimelineKey}) → {destination.DisplayName} ({destination.Key})",
            HasCustomTimeline = hasCustomTimeline,
        };
    }

    /// <summary>Moves one binding file onto the destination key; returns false when the file is dropped.</summary>
    private bool RetargetFile(
        RetargetPlan plan, string path, byte[] bytes, Dictionary<string, byte[]> files, AnimationRetargetReport report)
    {
        var binding = plan.Binding;
        var destination = plan.Assignment.Destination;

        if (path.EndsWith(".tmb", StringComparison.Ordinal))
        {
            if (!plan.HasCustomTimeline)
            {
                report.TimelinesDropped++;
                return false;
            }

            if (!plan.WarnedCustomTimeline)
            {
                plan.WarnedCustomTimeline = true;
                report.Warnings.Add(
                    $"{binding.DisplayName}: the mod's edited timeline is moved as-is; its internal references " +
                    "keep the mod's own animation clip names.");
            }
        }
        else if (!plan.HasCustomTimeline)
        {
            // The destination's vanilla timeline picks clips by name, so the
            // pap's fixed-width name fields are patched to the names the
            // destination expects (read from its vanilla pap).
            bytes = RenameToDestinationClips(path, bytes, plan, report);
        }

        var mapped = ReplaceKeyTail(path, binding.TimelineKey, destination.Key);
        if (files.ContainsKey(mapped))
            report.Warnings.Add($"Two mod files map onto {mapped}; the later one wins.");
        files[mapped] = bytes;
        report.FilesRewired++;
        return true;
    }

    private byte[] RenameToDestinationClips(string papPath, byte[] bytes, RetargetPlan plan, AnimationRetargetReport report)
    {
        var destination = plan.Assignment.Destination;
        var destNames = ReadVanillaClipNames(ReplaceKeyTail(papPath, plan.Binding.TimelineKey, destination.Key));
        if (destNames.Count == 0)
        {
            report.Warnings.Add(
                $"The game ships no {destination.Key} animation matching {papPath}; " +
                "the file keeps its original clip names and may not play.");
            return bytes;
        }

        try
        {
            var index = -1;
            var renamed = PapAnimations.RenameAnimations(bytes, name =>
            {
                index++;
                if (index >= destNames.Count)
                    return null;
                return destNames[index] == name ? null : destNames[index];
            });
            if (!ReferenceEquals(renamed, bytes))
                report.PapsRenamed++;
            if (index + 1 > destNames.Count)
                report.Warnings.Add(
                    $"{papPath} holds {index + 1} animation clips but {destination.Key} only names {destNames.Count}; " +
                    "the extra clips keep their names.");
            return renamed;
        }
        catch (PapParseException ex)
        {
            report.Warnings.Add($"{papPath} could not be patched ({ex.Message}): clip names kept as-is.");
            return bytes;
        }
    }

    /// <summary>
    /// The clip names of the game's own pap for this path, with a c0101
    /// fallback for races the game ships no file for (names are identical
    /// across races).
    /// </summary>
    private IReadOnlyList<string> ReadVanillaClipNames(string papPath)
    {
        foreach (var candidate in new[] { papPath, RaceDirRegex().Replace(papPath, "/c0101/") }.Distinct())
        {
            if (_gameData.Lumina.GetFile(candidate)?.Data is not { } data)
                continue;
            try
            {
                return PapAnimations.ReadNames(data);
            }
            catch (PapParseException)
            {
            }
        }

        return [];
    }

    private bool IsVanillaTimeline(string path, byte[] modBytes)
    {
        var vanilla = _gameData.Lumina.GetFile(path)?.Data;
        return vanilla is not null && vanilla.AsSpan().SequenceEqual(modBytes);
    }

    /// <summary>chara/…/bt_common/emote/dance.pap + "emote/dance" → "emote/sway" ⇒ chara/…/bt_common/emote/sway.pap.</summary>
    internal static string ReplaceKeyTail(string path, string sourceKey, string destinationKey)
    {
        var extension = Path.GetExtension(path);
        var tail = "/" + sourceKey + extension;
        if (!path.EndsWith(tail, StringComparison.Ordinal))
            throw new AnimationRetargetException($"{path} does not belong to the {sourceKey} timeline.");
        return path[..^tail.Length] + "/" + destinationKey + extension;
    }

    [GeneratedRegex(@"/c(\d{4})/")]
    private static partial Regex RaceDirRegex();
}
