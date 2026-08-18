using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;

namespace Moonlace.Core.Session;

/// <summary>
/// File-backed session storage under the per-user local application data
/// directory (~/.local/share/Moonlace/sessions on Linux,
/// %LOCALAPPDATA%\Moonlace\sessions on Windows). Sessions persist across
/// launches; Discard removes the directory. Never touches the FFXIV
/// installation.
/// </summary>
public sealed class SessionService : ISessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SessionService> _logger;
    private readonly string _sessionsRoot;
    private readonly Lock _lock = new();

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
    }

    private static string DefaultSessionsRoot()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "Moonlace", "sessions");
    }

    public EquipmentItem? ActiveItem
    {
        get
        {
            lock (_lock)
                return _activeItem;
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
        var manifestFile = Path.Combine(SessionDir(item.RowId), "manifest.json");
        try
        {
            if (File.Exists(manifestFile))
            {
                var loaded = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestFile));
                if (loaded is not null)
                {
                    // Drop entries whose session file went missing (stale/corrupt data).
                    var valid = loaded.Entries
                        .Where(e => File.Exists(Path.Combine(SessionDir(item.RowId), e.FileName)))
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
        uint itemId;
        lock (_lock)
        {
            if (_activeItem is null)
                return null;
            itemId = _activeItem.RowId;
            file = _manifest.Entries.FirstOrDefault(e => e.GamePath == gamePath)?.FileName;
        }

        if (file is null)
            return null;

        try
        {
            return File.ReadAllBytes(Path.Combine(SessionDir(itemId), file));
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

            var dir = SessionDir(_activeItem.RowId);
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

            var dir = SessionDir(_activeItem.RowId);
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Discarded session for item {Id}", _activeItem.RowId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete session directory {Dir}", dir);
            }

            _manifest = new SessionManifest { ItemRowId = _activeItem.RowId, ItemName = _activeItem.Name };
        }

        SessionChanged?.Invoke();
    }

    private void SaveManifest(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(_manifest, JsonOptions));
    }

    private string SessionDir(uint itemRowId) => Path.Combine(_sessionsRoot, $"item-{itemRowId}");

    private static string SanitizeFileName(string gamePath) =>
        gamePath.Replace('/', '_').Replace('\\', '_');
}
