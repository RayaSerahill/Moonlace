namespace Moonlace.Core.Session;

public enum SessionAssetKind
{
    Model,
    Material,
    Texture,
}

/// <summary>
/// One modified asset inside a session: the logical game path it replaces,
/// the session file holding the modified bytes, and a revision that bumps on
/// every store (used to invalidate caches).
/// </summary>
public sealed record SessionEntry(string GamePath, SessionAssetKind Kind, string FileName, int Revision);

/// <summary>Serialized as manifest.json in the session directory.</summary>
public sealed class SessionManifest
{
    public uint ItemRowId { get; set; }

    public string ItemName { get; set; } = "";

    public List<SessionEntry> Entries { get; set; } = [];
}
