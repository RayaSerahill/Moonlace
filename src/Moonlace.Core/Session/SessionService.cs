using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;

namespace Moonlace.Core.Session;

/// <summary>
/// File-backed session storage under the per-user local application data
/// directory (~/.local/share/Moonlace/sessions on Linux,
/// %LOCALAPPDATA%\Moonlace\sessions on Windows). Layout:
/// sessions/&lt;session-id&gt;/item-&lt;rowid&gt;/…. Every launch starts a new
/// session; older ones stay connectable until retention prunes them. A
/// session's directory is only created once something is stored, so
/// launches without edits leave nothing behind. Never touches the FFXIV
/// installation.
/// </summary>
public sealed class SessionService : ISessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string MetadataFileName = "session.json";

    private readonly ILogger<SessionService> _logger;
    private readonly string _sessionsRoot;
    private readonly Lock _lock = new();

    private string _currentSessionId;
    private EquipmentItem? _activeItem;
    private SessionManifest _manifest = new();

    public event Action? SessionChanged;

    public SessionService(ILogger<SessionService> logger)
        : this(logger, DefaultSessionsRoot())
    {
    }

    /// <summary>Test hook: explicit storage root.</summary>
    public SessionService(ILogger<SessionService> logger, string sessionsRoot)
    {
        _logger = logger;
        _sessionsRoot = sessionsRoot;
        MigrateLegacyLayout();
        _currentSessionId = NewSessionId();
    }

    private static string DefaultSessionsRoot()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "Moonlace", "sessions");
    }

    /// <summary>
    /// Pre-2.3 builds stored item directories directly under the sessions
    /// root. Wrap any such directories into a single session so those edits
    /// remain connectable.
    /// </summary>
    private void MigrateLegacyLayout()
    {
        try
        {
            if (!Directory.Exists(_sessionsRoot))
                return;

            var legacyDirs = Directory.GetDirectories(_sessionsRoot, "item-*");
            if (legacyDirs.Length == 0)
                return;

            var sessionDir = Path.Combine(_sessionsRoot, NewSessionId());
            Directory.CreateDirectory(sessionDir);
            var oldest = DateTime.UtcNow;
            var newest = DateTime.MinValue;
            foreach (var dir in legacyDirs)
            {
                var stamp = Directory.GetLastWriteTimeUtc(dir);
                oldest = stamp < oldest ? stamp : oldest;
                newest = stamp > newest ? stamp : newest;
                Directory.Move(dir, Path.Combine(sessionDir, Path.GetFileName(dir)));
            }

            WriteMetadata(sessionDir, new SessionMetadata { CreatedAtUtc = oldest, LastUsedAtUtc = newest });
            _logger.LogInformation("Migrated {Count} pre-session item directories into session {Id}",
                legacyDirs.Length, Path.GetFileName(sessionDir));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy session layout under {Root}", _sessionsRoot);
        }
    }

    private string NewSessionId()
    {
        var baseId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var id = baseId;
        for (var n = 2; id == _currentSessionId || Directory.Exists(Path.Combine(_sessionsRoot, id)); n++)
            id = $"{baseId}-{n}";
        return id;
    }

    public EquipmentItem? ActiveItem
    {
        get
        {
            lock (_lock)
                return _activeItem;
        }
    }

    public string CurrentSessionId
    {
        get
        {
            lock (_lock)
                return _currentSessionId;
        }
    }

    public bool IsDirty
    {
        get
        {
            lock (_lock)
                return _manifest.Entries.Count > 0;
        }
    }

    public IReadOnlyList<SessionEntry> Entries
    {
        get
        {
            lock (_lock)
                return _manifest.Entries.ToArray();
        }
    }

    public void ActivateForItem(EquipmentItem? item)
    {
        lock (_lock)
        {
            if (item?.RowId == _activeItem?.RowId)
            {
                _activeItem = item;
                return;
            }

            _activeItem = item;
            _manifest = item is null ? new SessionManifest() : LoadManifest(item);
        }

        SessionChanged?.Invoke();
    }

    private SessionManifest LoadManifest(EquipmentItem item)
    {
        var itemDir = ItemDir(item.RowId);
        var manifestFile = Path.Combine(itemDir, "manifest.json");
        try
        {
            if (File.Exists(manifestFile))
            {
                var loaded = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestFile));
                if (loaded is not null)
                {
                    // Drop entries whose session file went missing (stale/corrupt data).
                    var valid = loaded.Entries
                        .Where(e => File.Exists(Path.Combine(itemDir, e.FileName)))
                        .ToList();
                    if (valid.Count != loaded.Entries.Count)
                        _logger.LogWarning("Session for item {Id} had {Missing} missing files; ignoring them",
                            item.RowId, loaded.Entries.Count - valid.Count);
                    loaded.Entries = valid;
                    _logger.LogInformation("Loaded session for item {Id} with {Count} modified assets",
                        item.RowId, valid.Count);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session manifest for item {Id}; starting clean", item.RowId);
        }

        return new SessionManifest { ItemRowId = item.RowId, ItemName = item.Name };
    }

    public byte[]? TryReadAsset(string gamePath)
    {
        string? file;
        string itemDir;
        lock (_lock)
        {
            if (_activeItem is null)
                return null;
            itemDir = ItemDir(_activeItem.RowId);
            file = _manifest.Entries.FirstOrDefault(e => e.GamePath == gamePath)?.FileName;
        }

        if (file is null)
            return null;

        try
        {
            return File.ReadAllBytes(Path.Combine(itemDir, file));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read session asset {Path}", gamePath);
            return null;
        }
    }

    public int GetRevision(string gamePath)
    {
        lock (_lock)
            return _manifest.Entries.FirstOrDefault(e => e.GamePath == gamePath)?.Revision ?? 0;
    }

    public SessionEntry StoreAsset(string gamePath, SessionAssetKind kind, byte[] data)
    {
        SessionEntry entry;
        lock (_lock)
        {
            if (_activeItem is null)
                throw new InvalidOperationException("No active session; select an item first.");

            var dir = ItemDir(_activeItem.RowId);
            Directory.CreateDirectory(dir);

            var existing = _manifest.Entries.FirstOrDefault(e => e.GamePath == gamePath);
            var fileName = existing?.FileName ?? SanitizeFileName(gamePath);
            entry = new SessionEntry(gamePath, kind, fileName, (existing?.Revision ?? 0) + 1);

            File.WriteAllBytes(Path.Combine(dir, fileName), data);

            if (existing is not null)
                _manifest.Entries.Remove(existing);
            _manifest.Entries.Add(entry);
            _manifest.ItemRowId = _activeItem.RowId;
            _manifest.ItemName = _activeItem.Name;
            SaveManifest(dir);
            TouchMetadata(SessionDir(_currentSessionId));

            _logger.LogInformation("Session: stored {Kind} {Path} ({Bytes} bytes, revision {Rev})",
                kind, gamePath, data.Length, entry.Revision);
        }

        SessionChanged?.Invoke();
        return entry;
    }

    public void DiscardActiveSession()
    {
        lock (_lock)
        {
            if (_activeItem is null)
                return;

            var dir = ItemDir(_activeItem.RowId);
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                DeleteSessionDirIfEmpty(SessionDir(_currentSessionId));
                _logger.LogInformation("Discarded session edits for item {Id}", _activeItem.RowId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete session directory {Dir}", dir);
            }

            _manifest = new SessionManifest { ItemRowId = _activeItem.RowId, ItemName = _activeItem.Name };
        }

        SessionChanged?.Invoke();
    }

    public void StartNewSession()
    {
        lock (_lock)
        {
            _currentSessionId = NewSessionId();
            _manifest = _activeItem is null
                ? new SessionManifest()
                : new SessionManifest { ItemRowId = _activeItem.RowId, ItemName = _activeItem.Name };
            _logger.LogInformation("Started new session {Id}", _currentSessionId);
        }

        SessionChanged?.Invoke();
    }

    public void ConnectToSession(string sessionId)
    {
        lock (_lock)
        {
            var dir = SessionDir(sessionId);
            if (!Directory.Exists(dir))
                throw new InvalidOperationException($"Session \"{sessionId}\" no longer exists.");

            DeleteSessionDirIfEmpty(SessionDir(_currentSessionId));
            _currentSessionId = sessionId;
            TouchMetadata(dir);
            _manifest = _activeItem is null ? new SessionManifest() : LoadManifest(_activeItem);
            _logger.LogInformation("Connected to session {Id}", sessionId);
        }

        SessionChanged?.Invoke();
    }

    public IReadOnlyList<SessionInfo> ListPreviousSessions()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_sessionsRoot))
                return [];

            var sessions = new List<SessionInfo>();
            foreach (var dir in Directory.GetDirectories(_sessionsRoot))
            {
                var id = Path.GetFileName(dir);
                if (id == _currentSessionId)
                    continue;

                var itemNames = new List<string>();
                var fileCount = 0;
                long totalBytes = 0;
                foreach (var manifest in ReadItemManifests(dir))
                {
                    if (manifest.Entries.Count == 0)
                        continue;
                    itemNames.Add(string.IsNullOrEmpty(manifest.ItemName)
                        ? $"Item {manifest.ItemRowId}"
                        : manifest.ItemName);
                    fileCount += manifest.Entries.Count;
                    foreach (var entry in manifest.Entries)
                    {
                        var file = new FileInfo(Path.Combine(dir, $"item-{manifest.ItemRowId}", entry.FileName));
                        totalBytes += file.Exists ? file.Length : 0;
                    }
                }

                if (fileCount == 0)
                    continue;

                var meta = ReadMetadata(dir);
                sessions.Add(new SessionInfo(id, meta.CreatedAtUtc, meta.LastUsedAtUtc, fileCount, totalBytes, itemNames));
            }

            return sessions.OrderByDescending(s => s.LastUsedAtUtc).ToArray();
        }
    }

    public IReadOnlyList<TouchedAsset> GetTouchedAssets()
    {
        lock (_lock)
        {
            var dir = SessionDir(_currentSessionId);
            if (!Directory.Exists(dir))
                return [];

            return ReadItemManifests(dir)
                .SelectMany(m => m.Entries.Select(e => new TouchedAsset(
                    string.IsNullOrEmpty(m.ItemName) ? $"Item {m.ItemRowId}" : m.ItemName,
                    m.ItemRowId, e.GamePath, e.Kind, e.Revision)))
                .OrderBy(a => a.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.GamePath, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public int PruneExpiredSessions(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        lock (_lock)
        {
            if (!Directory.Exists(_sessionsRoot))
                return 0;

            foreach (var dir in Directory.GetDirectories(_sessionsRoot))
            {
                if (Path.GetFileName(dir) == _currentSessionId)
                    continue;
                if (ReadMetadata(dir).LastUsedAtUtc >= cutoff)
                    continue;

                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to prune expired session {Dir}", dir);
                }
            }
        }

        if (removed > 0)
            _logger.LogInformation("Pruned {Count} expired sessions (unused for more than {Age})", removed, maxAge);
        return removed;
    }

    private IEnumerable<SessionManifest> ReadItemManifests(string sessionDir)
    {
        foreach (var itemDir in Directory.GetDirectories(sessionDir, "item-*"))
        {
            var manifestFile = Path.Combine(itemDir, "manifest.json");
            if (!File.Exists(manifestFile))
                continue;

            SessionManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestFile));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable session manifest {File}", manifestFile);
            }

            if (manifest is not null)
                yield return manifest;
        }
    }

    private SessionMetadata ReadMetadata(string sessionDir)
    {
        var file = Path.Combine(sessionDir, MetadataFileName);
        try
        {
            if (File.Exists(file))
            {
                var meta = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(file));
                if (meta is not null)
                    return meta;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read session metadata {File}", file);
        }

        // Sessions without metadata (interrupted writes) fall back to directory timestamps.
        var stamp = Directory.GetLastWriteTimeUtc(sessionDir);
        return new SessionMetadata { CreatedAtUtc = stamp, LastUsedAtUtc = stamp };
    }

    private void TouchMetadata(string sessionDir)
    {
        try
        {
            var meta = File.Exists(Path.Combine(sessionDir, MetadataFileName))
                ? ReadMetadata(sessionDir)
                : new SessionMetadata { CreatedAtUtc = DateTime.UtcNow };
            meta.LastUsedAtUtc = DateTime.UtcNow;
            WriteMetadata(sessionDir, meta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update session metadata in {Dir}", sessionDir);
        }
    }

    private static void WriteMetadata(string sessionDir, SessionMetadata meta)
    {
        File.WriteAllText(Path.Combine(sessionDir, MetadataFileName), JsonSerializer.Serialize(meta, JsonOptions));
    }

    private void DeleteSessionDirIfEmpty(string sessionDir)
    {
        try
        {
            if (Directory.Exists(sessionDir) && Directory.GetDirectories(sessionDir, "item-*").Length == 0)
                Directory.Delete(sessionDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove empty session directory {Dir}", sessionDir);
        }
    }

    private void SaveManifest(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(_manifest, JsonOptions));
    }

    private string SessionDir(string sessionId) => Path.Combine(_sessionsRoot, sessionId);

    private string ItemDir(uint itemRowId) => Path.Combine(_sessionsRoot, _currentSessionId, $"item-{itemRowId}");

    private static string SanitizeFileName(string gamePath) =>
        gamePath.Replace('/', '_').Replace('\\', '_');
}
