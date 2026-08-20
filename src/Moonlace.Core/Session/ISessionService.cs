using Moonlace.Core.Models;

namespace Moonlace.Core.Session;

/// <summary>
/// The non-destructive editing workspace. The FFXIV installation is immutable
/// input; every modification is stored as a copy-on-write session asset here.
///
/// A session is one editing run: each application launch starts a fresh one,
/// and it holds the per-item edits made while it is current. Previous
/// sessions stay on disk and can be reconnected from the Sessions menu until
/// the retention policy expires them. Within the current session, the
/// per-item store for the currently selected item is "active" and is what
/// effective-asset resolution consults.
/// </summary>
public interface ISessionService
{
    /// <summary>Raised whenever the active session's contents change (store, discard, or session switch).</summary>
    event Action? SessionChanged;

    EquipmentItem? ActiveItem { get; }

    /// <summary>Identifier of the current session. Its directory exists only once something is stored.</summary>
    string CurrentSessionId { get; }

    /// <summary>Switches the active session to the given item (loading any persisted state). Null deactivates.</summary>
    void ActivateForItem(EquipmentItem? item);

    /// <summary>True when the active item has modifications in the current session.</summary>
    bool IsDirty { get; }

    /// <summary>Modified assets of the active item in the current session.</summary>
    IReadOnlyList<SessionEntry> Entries { get; }

    /// <summary>Bytes of the session copy for a game path, or null when the asset is unmodified.</summary>
    byte[]? TryReadAsset(string gamePath);

    /// <summary>0 when the game original is effective; otherwise the session entry's revision.</summary>
    int GetRevision(string gamePath);

    /// <summary>Creates or replaces the session copy for a game path (copy-on-write write side).</summary>
    SessionEntry StoreAsset(string gamePath, SessionAssetKind kind, byte[] data);

    /// <summary>Removes all session copies of the active item and restores the original assets.</summary>
    void DiscardActiveSession();

    /// <summary>
    /// Begins a fresh, empty session; the worktree is clear afterwards. The
    /// previous session (if it stored anything) remains connectable.
    /// </summary>
    void StartNewSession();

    /// <summary>
    /// Makes a previous session current again, so its edits become effective
    /// and further edits land in it. Throws when the session no longer exists.
    /// </summary>
    void ConnectToSession(string sessionId);

    /// <summary>Stored sessions other than the current one, most recently used first. Empty sessions are omitted.</summary>
    IReadOnlyList<SessionInfo> ListPreviousSessions();

    /// <summary>Every asset touched in the current session, across all items.</summary>
    IReadOnlyList<TouchedAsset> GetTouchedAssets();

    /// <summary>
    /// Deletes stored sessions (never the current one) whose last use is
    /// older than <paramref name="maxAge"/>. Returns how many were removed.
    /// </summary>
    int PruneExpiredSessions(TimeSpan maxAge);
}
