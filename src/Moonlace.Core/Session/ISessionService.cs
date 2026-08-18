using Moonlace.Core.Models;

namespace Moonlace.Core.Session;

/// <summary>
/// The non-destructive editing workspace. The FFXIV installation is immutable
/// input; every modification is stored as a copy-on-write session asset here.
/// One session exists per game item; the session for the currently selected
/// item is "active" and is what effective-asset resolution consults.
/// </summary>
public interface ISessionService
{
    /// <summary>Raised whenever the active session's contents change (store or discard).</summary>
    event Action? SessionChanged;

    EquipmentItem? ActiveItem { get; }

    /// <summary>Switches the active session to the given item (loading any persisted state). Null deactivates.</summary>
    void ActivateForItem(EquipmentItem? item);

    /// <summary>True when the active session contains modifications.</summary>
    bool IsDirty { get; }

    /// <summary>Modified assets in the active session.</summary>
    IReadOnlyList<SessionEntry> Entries { get; }

    /// <summary>Bytes of the session copy for a game path, or null when the asset is unmodified.</summary>
    byte[]? TryReadAsset(string gamePath);

    /// <summary>0 when the game original is effective; otherwise the session entry's revision.</summary>
    int GetRevision(string gamePath);

    /// <summary>Creates or replaces the session copy for a game path (copy-on-write write side).</summary>
    SessionEntry StoreAsset(string gamePath, SessionAssetKind kind, byte[] data);

    /// <summary>Removes all session copies of the active item and restores the original assets.</summary>
    void DiscardActiveSession();
}
