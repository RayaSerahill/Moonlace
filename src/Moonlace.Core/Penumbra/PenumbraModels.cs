namespace Moonlace.Core.Penumbra;

/// <summary>A linking/parsing problem with a Penumbra mod folder, phrased for the UI.</summary>
public sealed class PenumbraLinkException(string message) : Exception(message);

public enum PenumbraGroupType
{
    Single,
    Multi,
}

/// <summary>One selectable option inside a group: a name plus its game-path → mod-relative-path redirections.</summary>
public sealed class PenumbraOption
{
    public required string Name { get; init; }

    /// <summary>Option priority inside a Multi group (higher overrides); 0 when absent.</summary>
    public required int Priority { get; init; }

    /// <summary>Game path → path relative to the mod directory (normalized to '/'). Empty for no-op options ("Vanilla").</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }
}

/// <summary>
/// An option group, from meta.json's Groups array (FileVersion 4) or a
/// group_*.json file (FileVersion 3). Imc groups are not represented.
/// </summary>
public sealed class PenumbraGroup
{
    public required string Name { get; init; }

    public required PenumbraGroupType Type { get; init; }

    /// <summary>Penumbra group priority; higher-priority groups override lower ones in the file map.</summary>
    public required int Priority { get; init; }

    /// <summary>Penumbra's stored default: option index for Single groups, option bitmask for Multi groups.</summary>
    public required long DefaultSettings { get; init; }

    public required IReadOnlyList<PenumbraOption> Options { get; init; }

    /// <summary>The group_*.json file this group came from (legacy layout only; null for meta.json groups).</summary>
    public string? SourceFile { get; init; }

    /// <summary>Selected option indices per Penumbra's DefaultSettings encoding.</summary>
    public IReadOnlyList<int> DefaultSelection()
    {
        if (Type == PenumbraGroupType.Single)
        {
            var index = (int)Math.Clamp(DefaultSettings, 0, Math.Max(0, Options.Count - 1));
            return Options.Count == 0 ? [] : [index];
        }

        return Enumerable.Range(0, Options.Count)
            .Where(i => (DefaultSettings & (1L << i)) != 0)
            .ToArray();
    }
}

/// <summary>What a Penumbra mod folder contains, as far as live editing cares.</summary>
public sealed class PenumbraModInfo
{
    public required string Directory { get; init; }

    public required string Name { get; init; }

    /// <summary>default_mod.json redirections (game path → mod-relative path, normalized to '/').</summary>
    public required IReadOnlyDictionary<string, string> DefaultFiles { get; init; }

    /// <summary>Groups in group-file order (display order); map precedence uses their Priority.</summary>
    public required IReadOnlyList<PenumbraGroup> Groups { get; init; }
}

/// <summary>An option edits are being captured into: identified by group and option name.</summary>
public sealed record PenumbraEditTarget(string Group, string Option);

/// <summary>
/// Live-edit link into a Penumbra mod folder. While linked, effective-asset
/// reads resolve through the mod's file redirections (for the selected
/// options) and edits are written directly into the mod folder — after the
/// original file has been backed up, so the whole editing run can be
/// reverted. The FFXIV installation stays read-only regardless.
/// </summary>
public interface IPenumbraLinkService
{
    /// <summary>Raised on link, unlink, option changes, writes and revert.</summary>
    event Action? LinkChanged;

    bool IsLinked { get; }

    string? ModName { get; }

    string? ModDirectory { get; }

    /// <summary>The linked mod's option groups (empty when unlinked).</summary>
    IReadOnlyList<PenumbraGroup> Groups { get; }

    /// <summary>Selected option indices, parallel to <see cref="Groups"/>.</summary>
    IReadOnlyList<IReadOnlyList<int>> Selection { get; }

    /// <summary>Number of mod files changed (backed up) during this editing run.</summary>
    int ChangedFileCount { get; }

    /// <summary>Parses a mod folder without linking it — used to present the option choices first.</summary>
    PenumbraModInfo Inspect(string directory);

    /// <summary>Links a mod folder for live editing with the given option selection.</summary>
    void Link(string directory, IReadOnlyList<IReadOnlyList<int>> selection);

    /// <summary>Changes the selected options of the linked mod. Files already edited on disk are untouched.</summary>
    void SetSelection(IReadOnlyList<IReadOnlyList<int>> selection);

    /// <summary>Detaches from the mod. Edits (and their backups) stay in the mod folder.</summary>
    void Unlink();

    /// <summary>Restores every backed-up file and deletes files Moonlace created; the mod returns to its pre-edit state.</summary>
    void RevertAll();

    /// <summary>Bytes of the mod file a game path redirects to, or null when the mod does not cover that path.</summary>
    byte[]? TryReadAsset(string gamePath);

    /// <summary>0 when the game original is effective; otherwise a cache-busting revision for the mod-mapped file.</summary>
    int GetRevision(string gamePath);

    /// <summary>True when the mod file behind this game path was changed during this editing run.</summary>
    bool IsChanged(string gamePath);

    /// <summary>
    /// Writes edited bytes to the mod file behind a game path, backing the
    /// original up first. Paths the mod does not cover are added to the mod's
    /// default_mod.json (that JSON is backed up too, so revert removes them).
    /// With an edit target set, edits are captured as that option's own files
    /// instead — the default files stay untouched.
    /// </summary>
    void WriteAsset(string gamePath, byte[] data);

    // --- Authoring options and groups ---

    /// <summary>Adds an empty option group to the linked mod (the mod JSON is backed up first).</summary>
    void AddGroup(string name, PenumbraGroupType type);

    /// <summary>Adds an empty option to a group. Its assets keep coming from the always-active default files until edited.</summary>
    void AddOption(string groupName, string optionName);

    /// <summary>
    /// The option edits are captured into, or null to edit the effective
    /// files in place (default files / existing option files).
    /// </summary>
    PenumbraEditTarget? EditTarget { get; }

    /// <summary>Targets an option for edit capture; the option is force-selected so its files are visible.</summary>
    void SetEditTarget(string groupName, string optionName);

    void ClearEditTarget();
}
