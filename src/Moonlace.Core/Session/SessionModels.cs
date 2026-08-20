namespace Moonlace.Core.Session;

public enum SessionAssetKind
{
    Model,
    Material,
    Texture,

    /// <summary>Anything else a modpack import can carry (avfx, scd, shpk, …).</summary>
    Other,
}

/// <summary>
/// One modified asset inside a session: the logical game path it replaces,
/// the session file holding the modified bytes, and a revision that bumps on
/// every store (used to invalidate caches).
/// </summary>
public sealed record SessionEntry(string GamePath, SessionAssetKind Kind, string FileName, int Revision);

/// <summary>Serialized as manifest.json in each per-item directory of a session.</summary>
public sealed class SessionManifest
{
    public uint ItemRowId { get; set; }

    public string ItemName { get; set; } = "";

    public List<SessionEntry> Entries { get; set; } = [];
}

/// <summary>Serialized as session.json in each session directory.</summary>
public sealed class SessionMetadata
{
    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastUsedAtUtc { get; set; }
}

/// <summary>A stored session, summarized for the "connect to previous session" list.</summary>
public sealed record SessionInfo(
    string Id,
    DateTime CreatedAtUtc,
    DateTime LastUsedAtUtc,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> ItemNames);

/// <summary>One asset touched during a session, with the item it belongs to.</summary>
public sealed record TouchedAsset(
    string ItemName,
    uint ItemRowId,
    string GamePath,
    SessionAssetKind Kind,
    int Revision);
