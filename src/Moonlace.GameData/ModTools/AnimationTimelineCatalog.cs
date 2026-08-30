using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;

namespace Moonlace.GameData.ModTools;

/// <summary>
/// One animation timeline: the ActionTimeline key that names both the .tmb
/// (chara/action/{key}.tmb) and the race paps (…/bt_common/{key}.pap), with
/// a human-friendly label resolved from the Emote sheet.
/// </summary>
public sealed record AnimationTimeline(string Key, string DisplayName)
{
    public string Label => $"{DisplayName} · {Key}";
}

/// <summary>
/// Lookup service over the ActionTimeline and Emote sheets: matches modded
/// game paths to the timeline key they belong to, labels keys with the
/// emote(s) that play them (or a friendly idle/sitting-pose name), and lists
/// the emote timelines a mod can be retargeted onto. Built lazily on first
/// use, after the game data is initialized.
/// </summary>
public sealed partial class AnimationTimelineCatalog
{
    /// <summary>Folders whose timelines are offered as retarget destinations even when no emote references them (idle and sitting poses live here).</summary>
    private static readonly string[] DestinationFolders = ["emote", "emote_sp", "emote_ajust"];

    private readonly LuminaGameDataService _gameData;
    private readonly Lock _gate = new();
    private Loaded? _loaded;

    private sealed record Loaded(
        HashSet<string> AllKeys,
        Dictionary<string, string> DisplayNames,
        IReadOnlyList<AnimationTimeline> Destinations);

    public AnimationTimelineCatalog(LuminaGameDataService gameData)
    {
        _gameData = gameData;
    }

    /// <summary>Every retargetable destination: emote-referenced timelines plus the emote folders, friendliest labels first.</summary>
    public IReadOnlyList<AnimationTimeline> Destinations => Load().Destinations;

    public bool IsKnownKey(string key) => Load().AllKeys.Contains(key);

    /// <summary>Timeline keys outside the curated destinations, for raw key searches.</summary>
    public IEnumerable<string> SearchRawKeys(string fragment) =>
        Load().AllKeys.Where(k => k.Contains(fragment, StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    public AnimationTimeline Describe(string key) =>
        new(key, Load().DisplayNames.GetValueOrDefault(key) ?? FriendlyPoseName(key) ?? key);

    /// <summary>
    /// The timeline key a modded animation path belongs to, or null. Keys can
    /// contain slashes ("emote/dance"), so the longest known key that forms
    /// the path's tail wins: …/bt_common/emote/dance.pap and
    /// chara/action/emote/dance.tmb both match "emote/dance".
    /// </summary>
    public string? MatchKey(string gamePath)
    {
        var extension = Path.GetExtension(gamePath);
        if (extension is not (".pap" or ".tmb"))
            return null;

        var keys = Load().AllKeys;
        var segments = gamePath[..^extension.Length].Split('/');
        for (var take = Math.Min(4, segments.Length - 1); take >= 1; take--)
        {
            var candidate = string.Join('/', segments[^take..]);
            if (keys.Contains(candidate))
                return candidate;
        }

        return null;
    }

    private Loaded Load()
    {
        if (_loaded is { } loaded)
            return loaded;
        lock (_gate)
            return _loaded ??= Build();
    }

    private Loaded Build()
    {
        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var timeline in _gameData.Lumina.GetExcelSheet<ActionTimeline>()!)
        {
            var key = timeline.Key.ExtractText().ToLowerInvariant();
            if (key.Length > 0)
                allKeys.Add(key);
        }

        // Emote names per key; a key played by several emotes lists the first
        // and a count. Nameless emote rows (the /cpose families) contribute
        // nothing — those keys get their friendly pose names instead.
        var emoteNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var emote in _gameData.Lumina.GetExcelSheet<Emote>()!)
        {
            var name = emote.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;
            if (emote.TextCommand is { RowId: not 0, IsValid: true } command)
            {
                var slash = command.Value.Command.ExtractText();
                if (!string.IsNullOrEmpty(slash))
                    name = $"{name} ({slash})";
            }

            foreach (var timeline in emote.ActionTimeline)
            {
                if (timeline.RowId == 0 || !timeline.IsValid)
                    continue;
                var key = timeline.Value.Key.ExtractText().ToLowerInvariant();
                if (key.Length == 0)
                    continue;
                if (!emoteNames.TryGetValue(key, out var names))
                    emoteNames[key] = names = [];
                if (!names.Contains(name))
                    names.Add(name);
            }
        }

        var displayNames = emoteNames.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count == 1 ? kv.Value[0] : $"{kv.Value[0]} +{kv.Value.Count - 1}",
            StringComparer.Ordinal);

        var destinations = allKeys
            .Where(k => displayNames.ContainsKey(k)
                || DestinationFolders.Any(f => k.StartsWith(f + "/", StringComparison.Ordinal)))
            .Select(k => new AnimationTimeline(k, displayNames.GetValueOrDefault(k) ?? FriendlyPoseName(k) ?? k))
            .OrderBy(t => t.DisplayName == t.Key)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Loaded(allKeys, displayNames, destinations);
    }

    /// <summary>"emote/j_pose02_loop" → "Chair sitting pose 3", matching how players number the /cpose cycles (1-based).</summary>
    internal static string? FriendlyPoseName(string key)
    {
        var match = PoseKeyRegex().Match(key);
        if (!match.Success)
            return null;

        var family = match.Groups[1].Value switch
        {
            "j_" => "Chair sitting pose",
            "s_" => "Ground sitting pose",
            "l_" => "Dozing pose",
            _ => "Standing idle pose",
        };
        var number = int.Parse(match.Groups[2].Value) + 1;
        var phase = match.Groups[3].Value is "loop" ? "" : $" ({match.Groups[3].Value})";
        return $"{family} {number}{phase}";
    }

    [GeneratedRegex(@"^emote/(j_|s_|l_)?pose(\d+)_(loop|start|end)$")]
    private static partial Regex PoseKeyRegex();
}
