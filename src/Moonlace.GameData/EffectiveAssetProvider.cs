using Moonlace.Core.Penumbra;
using Moonlace.Core.Session;

namespace Moonlace.GameData;

/// <summary>
/// The single place that answers "which bytes should Moonlace use for this
/// game path right now". Normally: the active session's copy when one exists,
/// otherwise the original from the game archive. While a Penumbra live-edit
/// link is active, the linked mod's redirections take the session's place —
/// mod file when the mod covers the path, game original otherwise. Everything
/// downstream — rendering, tabs, exports, PMP packaging — reads through this.
/// </summary>
public sealed class EffectiveAssetProvider
{
    private readonly LuminaGameDataService _gameData;
    private readonly ISessionService _session;
    private readonly IPenumbraLinkService _link;

    public EffectiveAssetProvider(LuminaGameDataService gameData, ISessionService session, IPenumbraLinkService link)
    {
        _gameData = gameData;
        _session = session;
        _link = link;
    }

    /// <summary>Effective bytes for a game path, or null when the asset exists nowhere.</summary>
    public byte[]? TryReadFile(string gamePath)
    {
        var overrideBytes = _link.IsLinked
            ? _link.TryReadAsset(gamePath)
            : _session.TryReadAsset(gamePath);
        if (overrideBytes is not null)
            return overrideBytes;

        return _gameData.Lumina.FileExists(gamePath)
            ? _gameData.Lumina.GetFile(gamePath)?.Data
            : null;
    }

    /// <summary>
    /// True when the path resolves to any effective content: a game original,
    /// a session copy, or a linked-mod file — including files that exist only
    /// as edits (e.g. a newly created race-variant model).
    /// </summary>
    public bool FileExists(string gamePath) =>
        _gameData.Lumina.FileExists(gamePath) || Revision(gamePath) > 0;

    /// <summary>True when the effective asset was modified (session copy, or a live-edited mod file).</summary>
    public bool IsModified(string gamePath) =>
        _link.IsLinked ? _link.IsChanged(gamePath) : _session.GetRevision(gamePath) > 0;

    /// <summary>0 for the original game asset, otherwise a non-zero revision. Useful as a cache-key component.</summary>
    public int Revision(string gamePath) =>
        _link.IsLinked ? _link.GetRevision(gamePath) : _session.GetRevision(gamePath);
}
