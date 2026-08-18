using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Moonlace.Core.Penumbra;

/// <summary>
/// Live-edit link into an installed Penumbra mod folder. Understands both mod
/// layouts: FileVersion 4 (DefaultData + Groups inline in meta.json) and the
/// legacy FileVersion 3 (default_mod.json + group_*.json). Every mod file is
/// backed up under .moonlace-backup/ before its first edit, and the backup
/// manifest persists on disk, so a revert is possible even after relinking.
/// </summary>
public sealed class PenumbraLinkService : IPenumbraLinkService
{
    private const string NewFilesDirName = "moonlace";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ILogger<PenumbraLinkService> _logger;
    private readonly Lock _lock = new();

    private PenumbraModInfo? _mod;
    private bool _singleFileLayout;
    private List<int[]> _selection = [];
    private Dictionary<string, string> _defaultFiles = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _fileMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _writeCounts = new(StringComparer.OrdinalIgnoreCase);
    private List<ModBackupEntry> _backups = [];
    private int _generation;
    private PenumbraEditTarget? _editTarget;

    public event Action? LinkChanged;

    public PenumbraLinkService(ILogger<PenumbraLinkService> logger)
    {
        _logger = logger;
    }

    public bool IsLinked
    {
        get
        {
            lock (_lock)
                return _mod is not null;
        }
    }

    public string? ModName
    {
        get
        {
            lock (_lock)
                return _mod?.Name;
        }
    }

    public string? ModDirectory
    {
        get
        {
            lock (_lock)
                return _mod?.Directory;
        }
    }

    public IReadOnlyList<PenumbraGroup> Groups
    {
        get
        {
            lock (_lock)
                return _mod?.Groups ?? [];
        }
    }

    public IReadOnlyList<IReadOnlyList<int>> Selection
    {
        get
        {
            lock (_lock)
                return _selection.Select(s => (IReadOnlyList<int>)s.ToArray()).ToArray();
        }
    }

    public int ChangedFileCount
    {
        get
        {
            lock (_lock)
                return _backups.Count;
        }
    }

    public PenumbraEditTarget? EditTarget
    {
        get
        {
            lock (_lock)
                return _editTarget;
        }
    }

    public PenumbraModInfo Inspect(string directory) => InspectCore(directory).Info;

    public void Link(string directory, IReadOnlyList<IReadOnlyList<int>> selection)
    {
        lock (_lock)
        {
            var (info, singleFile) = InspectCore(directory);
            _mod = info;
            _singleFileLayout = singleFile;
            _defaultFiles = new Dictionary<string, string>(info.DefaultFiles, StringComparer.OrdinalIgnoreCase);
            _selection = NormalizeSelection(selection, info.Groups);
            _writeCounts.Clear();
            _backups = ModBackups.Load(directory);
            _generation++;
            RebuildMapLocked();

            _logger.LogInformation(
                "Penumbra live edit linked: \"{Name}\" at {Dir} ({Files} redirections, {Groups} groups, {Backups} existing backups)",
                info.Name, directory, _fileMap.Count, info.Groups.Count, _backups.Count);
        }

        LinkChanged?.Invoke();
    }

    public void SetSelection(IReadOnlyList<IReadOnlyList<int>> selection)
    {
        lock (_lock)
        {
            if (_mod is null)
                throw new InvalidOperationException("No Penumbra mod is linked.");

            _selection = NormalizeSelection(selection, _mod.Groups);
            // An edit target that got deselected can no longer capture edits sensibly.
            if (_editTarget is { } target)
            {
                var found = FindOptionLocked(target.Group, target.Option);
                if (found is null || !_selection[found.Value.GroupIndex].Contains(found.Value.OptionIndex))
                    _editTarget = null;
            }

            _generation++;
            RebuildMapLocked();
            _logger.LogInformation("Penumbra options changed for \"{Name}\": now {Files} redirections",
                _mod.Name, _fileMap.Count);
        }

        LinkChanged?.Invoke();
    }

    public void Unlink()
    {
        lock (_lock)
        {
            if (_mod is null)
                return;

            _logger.LogInformation("Penumbra live edit unlinked from \"{Name}\" ({Changed} changed files remain in the mod)",
                _mod.Name, _backups.Count);
            _mod = null;
            _selection = [];
            _defaultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _writeCounts.Clear();
            _backups = [];
            _editTarget = null;
        }

        LinkChanged?.Invoke();
    }

    public void RevertAll()
    {
        lock (_lock)
        {
            if (_mod is null)
                throw new InvalidOperationException("No Penumbra mod is linked.");

            var modRoot = Path.GetFullPath(_mod.Directory);
            foreach (var entry in _backups)
            {
                var target = ResolveModPathLocked(entry.RelativePath);
                if (entry.Existed)
                {
                    var backup = ModBackups.BackupFilePath(_mod.Directory, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(backup, target, overwrite: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    PruneEmptyDirectories(modRoot, Path.GetDirectoryName(target)!);
                }
            }

            var backupRoot = Path.Combine(_mod.Directory, ModBackups.DirName);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);

            _logger.LogInformation("Reverted {Count} files of \"{Name}\" to their pre-edit state",
                _backups.Count, _mod.Name);

            _backups = [];
            _writeCounts.Clear();

            // The mod JSON may have been restored too — re-read it.
            ReloadModLocked();
        }

        LinkChanged?.Invoke();
    }

    /// <summary>
    /// Re-reads the mod JSON from disk after it changed (revert, group/option
    /// authoring, redirection registration), keeping the selection by index
    /// (all authoring appends, so existing indices stay valid) and dropping
    /// an edit target whose option no longer exists.
    /// </summary>
    private void ReloadModLocked()
    {
        var (info, singleFile) = InspectCore(_mod!.Directory);
        _mod = info;
        _singleFileLayout = singleFile;
        _defaultFiles = new Dictionary<string, string>(info.DefaultFiles, StringComparer.OrdinalIgnoreCase);
        _selection = NormalizeSelection(_selection.Select(s => (IReadOnlyList<int>)s).ToArray(), info.Groups);
        if (_editTarget is { } target && FindOptionLocked(target.Group, target.Option) is null)
            _editTarget = null;
        _generation++;
        RebuildMapLocked();
    }

    public byte[]? TryReadAsset(string gamePath)
    {
        string? path;
        lock (_lock)
        {
            if (_mod is null || MapPathLocked(gamePath) is not { } rel)
                return null;
            path = ResolveModPathLocked(rel);
        }

        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read mod file for {Path}", gamePath);
            return null;
        }
    }

    public int GetRevision(string gamePath)
    {
        lock (_lock)
        {
            if (_mod is null || MapPathLocked(gamePath) is null)
                return 0;
            return _generation * 1000 + _writeCounts.GetValueOrDefault(gamePath);
        }
    }

    public bool IsChanged(string gamePath)
    {
        lock (_lock)
        {
            if (_mod is null || MapPathLocked(gamePath) is not { } rel)
                return false;
            return _backups.Any(e => string.Equals(e.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void WriteAsset(string gamePath, byte[] data)
    {
        lock (_lock)
        {
            if (_mod is null)
                throw new InvalidOperationException("No Penumbra mod is linked.");

            var rel = _editTarget is { } editTarget
                ? ResolveWriteIntoOptionLocked(editTarget, gamePath)
                : ResolveWriteInPlaceLocked(gamePath);

            var target = ResolveModPathLocked(rel);
            BackupLocked(rel, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            _writeCounts[gamePath] = _writeCounts.GetValueOrDefault(gamePath) + 1;

            _logger.LogInformation("Live edit: wrote {Bytes} bytes to {Rel} (for {Path})", data.Length, rel, gamePath);
        }

        LinkChanged?.Invoke();
    }

    /// <summary>Default write mode: overwrite the file the game path is effectively redirected to.</summary>
    private string ResolveWriteInPlaceLocked(string gamePath)
    {
        if (MapPathLocked(gamePath) is { } rel)
            return rel;

        rel = NewFilesDirName + "/" + NormalizeGamePath(gamePath);
        RegisterDefaultFileLocked(gamePath, rel);
        return rel;
    }

    /// <summary>
    /// Edit-capture mode: the write lands in the targeted option's own files.
    /// The always-active default files (and other options) stay untouched, so
    /// toggling the option in Penumbra switches between the looks. The
    /// registration reuses the game-path key the mod already redirects (exact
    /// or material-variant match) so the option layers over the same key.
    /// </summary>
    private string ResolveWriteIntoOptionLocked(PenumbraEditTarget editTarget, string gamePath)
    {
        var found = FindOptionLocked(editTarget.Group, editTarget.Option)
            ?? throw new PenumbraLinkException(
                $"The targeted option “{editTarget.Group} / {editTarget.Option}” no longer exists in the mod.");

        var key = FindCanonicalKeyLocked(gamePath);
        if (found.Option.Files.TryGetValue(key, out var existing))
            return existing;

        var rel = $"{NewFilesDirName}/{SanitizePathSegment(editTarget.Group)}/{SanitizePathSegment(editTarget.Option)}/{NormalizeGamePath(key)}";
        RegisterOptionFileLocked(found.Group, found.Option.Name, key, rel);
        return rel;
    }

    /// <summary>
    /// The game-path key the mod already uses for this asset: exact match,
    /// else the same material name in another variant folder, else the
    /// requested path itself.
    /// </summary>
    private string FindCanonicalKeyLocked(string gamePath)
    {
        if (_fileMap.ContainsKey(gamePath))
            return NormalizeGamePath(gamePath);

        var wanted = MaterialVariantRegex.Match(NormalizeGamePath(gamePath));
        if (!wanted.Success)
            return NormalizeGamePath(gamePath);

        foreach (var key in _fileMap.Keys)
        {
            var candidate = MaterialVariantRegex.Match(key);
            if (candidate.Success
                && string.Equals(candidate.Groups[1].Value, wanted.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Groups[2].Value, wanted.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return NormalizeGamePath(gamePath);
    }

    // --- Mod parsing ---

    private (PenumbraModInfo Info, bool SingleFileLayout) InspectCore(string directory)
    {
        if (!Directory.Exists(directory))
            throw new PenumbraLinkException($"Directory not found: {directory}");

        var metaPath = Path.Combine(directory, "meta.json");
        if (!File.Exists(metaPath))
            throw new PenumbraLinkException(
                "This is not a Penumbra mod folder (meta.json is missing). " +
                "Select one mod's own folder inside Penumbra's root mod directory.");

        MetaJson meta;
        try
        {
            meta = JsonSerializer.Deserialize<MetaJson>(File.ReadAllText(metaPath), ReadOptions)
                ?? throw new PenumbraLinkException("meta.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new PenumbraLinkException($"Could not parse meta.json: {ex.Message}");
        }

        var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(directory.TrimEnd('/', '\\')) : meta.Name.Trim();
        var singleFile = meta.DefaultData is not null || meta.Groups is not null;

        Dictionary<string, string> defaultFiles;
        List<PenumbraGroup> groups;
        if (singleFile)
        {
            defaultFiles = NormalizeFiles(meta.DefaultData?.Files);
            groups = ParseGroups(meta.Groups, "meta.json");
        }
        else
        {
            defaultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var defaultModPath = Path.Combine(directory, "default_mod.json");
            if (File.Exists(defaultModPath))
            {
                try
                {
                    var defaultMod = JsonSerializer.Deserialize<FilesContainerJson>(File.ReadAllText(defaultModPath), ReadOptions);
                    defaultFiles = NormalizeFiles(defaultMod?.Files);
                }
                catch (JsonException ex)
                {
                    throw new PenumbraLinkException($"Could not parse default_mod.json: {ex.Message}");
                }
            }

            groups = [];
            foreach (var groupFile in Directory.GetFiles(directory, "group_*.json")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<GroupJson>(File.ReadAllText(groupFile), ReadOptions);
                    groups.AddRange(ParseGroups(parsed is null ? null : [parsed],
                        Path.GetFileName(groupFile), Path.GetFileName(groupFile)));
                }
                catch (JsonException ex)
                {
                    throw new PenumbraLinkException($"Could not parse {Path.GetFileName(groupFile)}: {ex.Message}");
                }
            }
        }

        var info = new PenumbraModInfo
        {
            Directory = Path.GetFullPath(directory),
            Name = name,
            DefaultFiles = defaultFiles,
            Groups = groups,
        };
        return (info, singleFile);
    }

    private List<PenumbraGroup> ParseGroups(List<GroupJson>? groups, string source, string? sourceFile = null)
    {
        var result = new List<PenumbraGroup>();
        foreach (var group in groups ?? [])
        {
            var type = group.Type switch
            {
                "Single" => PenumbraGroupType.Single,
                "Multi" => PenumbraGroupType.Multi,
                _ => (PenumbraGroupType?)null,
            };
            if (type is null)
            {
                // Imc/Combining and future group types have no plain file
                // redirections Moonlace can edit through — leave them to Penumbra.
                _logger.LogWarning("Skipping unsupported group \"{Name}\" of type {Type} in {Source}",
                    group.Name, group.Type, source);
                continue;
            }

            var options = (group.Options ?? [])
                .Select(o => new PenumbraOption
                {
                    Name = o.Name ?? "(unnamed option)",
                    Priority = o.Priority,
                    Files = NormalizeFiles(o.Files),
                })
                .ToArray();

            // Zero-option groups are kept: freshly authored groups start empty.
            result.Add(new PenumbraGroup
            {
                Name = group.Name ?? "(unnamed group)",
                Type = type.Value,
                Priority = group.Priority,
                DefaultSettings = group.DefaultSettings,
                Options = options,
                SourceFile = sourceFile,
            });
        }

        return result;
    }

    private static Dictionary<string, string> NormalizeFiles(Dictionary<string, string>? files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, relPath) in files ?? [])
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(relPath))
                continue;
            result[NormalizeGamePath(gamePath)] = relPath.Replace('\\', '/').Trim();
        }

        return result;
    }

    private static string NormalizeGamePath(string gamePath) => gamePath.Replace('\\', '/').Trim();

    // --- File map ---

    private static readonly Regex MaterialVariantRegex = new(
        @"^(.*/material/)v\d{4}(/[^/]+\.mtrl)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Maps a game path to a mod-relative path. Materials get a fallback
    /// across variant folders: mods commonly pin their materials to one
    /// vNNNN folder and repoint the item there with an IMC manipulation,
    /// which Moonlace does not apply — so the same material name in any
    /// variant folder counts as covered.
    /// </summary>
    private string? MapPathLocked(string gamePath)
    {
        if (_fileMap.TryGetValue(gamePath, out var rel))
            return rel;

        var wanted = MaterialVariantRegex.Match(NormalizeGamePath(gamePath));
        if (!wanted.Success)
            return null;

        foreach (var (key, value) in _fileMap)
        {
            var candidate = MaterialVariantRegex.Match(key);
            if (candidate.Success
                && string.Equals(candidate.Groups[1].Value, wanted.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Groups[2].Value, wanted.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private void RebuildMapLocked()
    {
        var map = new Dictionary<string, string>(_defaultFiles, StringComparer.OrdinalIgnoreCase);

        // Layer selected options over the defaults in Penumbra precedence:
        // group priority, then option priority (Multi), later layers override.
        var layers = new List<(int GroupPriority, int OptionPriority, int GroupIndex, int OptionIndex, PenumbraOption Option)>();
        for (var gi = 0; gi < (_mod?.Groups.Count ?? 0); gi++)
        {
            var group = _mod!.Groups[gi];
            foreach (var oi in _selection[gi])
            {
                var option = group.Options[oi];
                var optionPriority = group.Type == PenumbraGroupType.Multi ? option.Priority : 0;
                layers.Add((group.Priority, optionPriority, gi, oi, option));
            }
        }

        foreach (var layer in layers
                     .OrderBy(l => l.GroupPriority)
                     .ThenBy(l => l.OptionPriority)
                     .ThenBy(l => l.GroupIndex)
                     .ThenBy(l => l.OptionIndex))
        {
            foreach (var (gamePath, rel) in layer.Option.Files)
                map[gamePath] = rel;
        }

        _fileMap = map;
    }

    private static List<int[]> NormalizeSelection(
        IReadOnlyList<IReadOnlyList<int>> selection, IReadOnlyList<PenumbraGroup> groups)
    {
        var result = new List<int[]>(groups.Count);
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            var requested = gi < selection.Count ? selection[gi] : null;
            if (requested is null)
            {
                result.Add([.. group.DefaultSelection()]);
                continue;
            }

            var valid = requested.Where(i => i >= 0 && i < group.Options.Count).Distinct().ToArray();
            if (group.Type == PenumbraGroupType.Single)
            {
                // A Single group always has exactly one option in effect
                // (unless the group is still empty).
                valid = valid.Length > 0 ? [valid[0]] : [.. group.DefaultSelection()];
                if (valid.Length == 0 && group.Options.Count > 0)
                    valid = [0];
            }

            result.Add(valid);
        }

        return result;
    }

    // --- Backups ---

    private void BackupLocked(string rel, string target)
    {
        if (ModBackups.EnsureBackedUp(_mod!.Directory, _backups, rel, target))
            _logger.LogInformation("Backed up mod file {Rel} (existed: {Existed})", rel, File.Exists(target));
    }

    // --- New-path registration ---

    /// <summary>
    /// Adds a game path the mod does not cover to its default file
    /// redirections, backing the mod JSON up first so revert removes the
    /// mapping again.
    /// </summary>
    private void RegisterDefaultFileLocked(string gamePath, string rel)
    {
        var jsonName = _singleFileLayout ? "meta.json" : "default_mod.json";
        var jsonPath = Path.Combine(_mod!.Directory, jsonName);
        BackupLocked(jsonName, jsonPath);

        JsonObject root;
        if (File.Exists(jsonPath))
        {
            root = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject
                ?? throw new PenumbraLinkException($"{jsonName} is not a JSON object.");
        }
        else
        {
            // Legacy layout without a default_mod.json — create a minimal one.
            root = new JsonObject
            {
                ["Name"] = "",
                ["Priority"] = 0,
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            };
        }

        var filesParent = root;
        if (_singleFileLayout)
        {
            if (root["DefaultData"] is not JsonObject defaultData)
                root["DefaultData"] = defaultData = new JsonObject();
            filesParent = defaultData;
        }

        if (filesParent["Files"] is not JsonObject files)
            filesParent["Files"] = files = new JsonObject();
        files[NormalizeGamePath(gamePath)] = rel.Replace('/', '\\');

        File.WriteAllText(jsonPath, root.ToJsonString(WriteOptions));

        _defaultFiles[gamePath] = rel;
        _fileMap[gamePath] = rel;
        _logger.LogInformation("Registered new redirection in {Json}: {Path} -> {Rel}", jsonName, gamePath, rel);
    }

    // --- Authoring options and groups ---

    public void AddGroup(string name, PenumbraGroupType type)
    {
        lock (_lock)
        {
            RequireLinkedLocked();
            name = name.Trim();
            if (name.Length == 0)
                throw new PenumbraLinkException("The group needs a name.");
            if (_mod!.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new PenumbraLinkException($"The mod already has a group named “{name}”.");

            if (_singleFileLayout)
            {
                var (root, path) = LoadModJsonLocked("meta.json");
                if (root["Groups"] is not JsonArray groups)
                    root["Groups"] = groups = new JsonArray();
                groups.Add(new JsonObject
                {
                    ["Type"] = type.ToString(),
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = name,
                    ["Priority"] = 0,
                    ["DefaultSettings"] = 0,
                    ["Options"] = new JsonArray(),
                });
                SaveModJson(path, root);
            }
            else
            {
                var fileName = NextGroupFileNameLocked(name);
                var path = Path.Combine(_mod.Directory, fileName);
                BackupLocked(fileName, path);
                SaveModJson(path, new JsonObject
                {
                    ["Name"] = name,
                    ["Description"] = "",
                    ["Priority"] = 0,
                    ["Type"] = type.ToString(),
                    ["DefaultSettings"] = 0,
                    ["Options"] = new JsonArray(),
                });
            }

            ReloadModLocked();
            _logger.LogInformation("Added {Type} group “{Name}” to \"{Mod}\"", type, name, _mod.Name);
        }

        LinkChanged?.Invoke();
    }

    public void AddOption(string groupName, string optionName)
    {
        lock (_lock)
        {
            RequireLinkedLocked();
            optionName = optionName.Trim();
            if (optionName.Length == 0)
                throw new PenumbraLinkException("The option needs a name.");
            var found = _mod!.Groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
                ?? throw new PenumbraLinkException($"Group “{groupName}” not found in the mod.");
            if (found.Options.Any(o => string.Equals(o.Name, optionName, StringComparison.OrdinalIgnoreCase)))
                throw new PenumbraLinkException($"Group “{groupName}” already has an option named “{optionName}”.");

            if (_singleFileLayout)
            {
                var (root, path) = LoadModJsonLocked("meta.json");
                var groupNode = FindGroupNode(root, found.Name);
                if (groupNode["Options"] is not JsonArray options)
                    groupNode["Options"] = options = new JsonArray();
                options.Add(new JsonObject
                {
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = optionName,
                });
                SaveModJson(path, root);
            }
            else
            {
                var fileName = found.SourceFile
                    ?? throw new PenumbraLinkException($"Group “{groupName}” has no source file.");
                var (root, path) = LoadModJsonLocked(fileName);
                if (root["Options"] is not JsonArray options)
                    root["Options"] = options = new JsonArray();
                options.Add(new JsonObject
                {
                    ["Name"] = optionName,
                    ["Description"] = "",
                    ["Files"] = new JsonObject(),
                    ["FileSwaps"] = new JsonObject(),
                    ["Manipulations"] = new JsonArray(),
                });
                SaveModJson(path, root);
            }

            ReloadModLocked();
            _logger.LogInformation("Added option “{Option}” to group “{Group}” of \"{Mod}\"",
                optionName, groupName, _mod.Name);
        }

        LinkChanged?.Invoke();
    }

    public void SetEditTarget(string groupName, string optionName)
    {
        lock (_lock)
        {
            RequireLinkedLocked();
            var found = FindOptionLocked(groupName, optionName)
                ?? throw new PenumbraLinkException($"Option “{groupName} / {optionName}” not found in the mod.");

            _editTarget = new PenumbraEditTarget(found.Group.Name, found.Option.Name);

            // The targeted option must be visible, or the user would edit blind.
            var (gi, oi) = (found.GroupIndex, found.OptionIndex);
            if (found.Group.Type == PenumbraGroupType.Single)
                _selection[gi] = [oi];
            else if (!_selection[gi].Contains(oi))
                _selection[gi] = [.. _selection[gi], oi];

            _generation++;
            RebuildMapLocked();
            _logger.LogInformation("Edits now captured in option “{Group} / {Option}”", groupName, optionName);
        }

        LinkChanged?.Invoke();
    }

    public void ClearEditTarget()
    {
        lock (_lock)
        {
            if (_editTarget is null)
                return;
            _editTarget = null;
            _logger.LogInformation("Edits now applied to the effective files in place");
        }

        LinkChanged?.Invoke();
    }

    /// <summary>Registers a game-path redirection inside an option's Files (mod JSON backed up first).</summary>
    private void RegisterOptionFileLocked(PenumbraGroup group, string optionName, string gamePath, string rel)
    {
        string path;
        JsonObject root;
        JsonObject optionParent;
        if (_singleFileLayout)
        {
            (root, path) = LoadModJsonLocked("meta.json");
            optionParent = FindGroupNode(root, group.Name);
        }
        else
        {
            var fileName = group.SourceFile
                ?? throw new PenumbraLinkException($"Group “{group.Name}” has no source file.");
            (root, path) = LoadModJsonLocked(fileName);
            optionParent = root;
        }

        var optionNode = FindOptionNode(optionParent, optionName);
        if (optionNode["Files"] is not JsonObject files)
            optionNode["Files"] = files = new JsonObject();
        files[NormalizeGamePath(gamePath)] = rel.Replace('/', '\\');
        SaveModJson(path, root);

        ReloadModLocked();
        _logger.LogInformation("Registered option redirection in {Json}: {Path} -> {Rel} ({Group} / {Option})",
            Path.GetFileName(path), gamePath, rel, group.Name, optionName);
    }

    private void RequireLinkedLocked()
    {
        if (_mod is null)
            throw new InvalidOperationException("No Penumbra mod is linked.");
    }

    private (PenumbraGroup Group, PenumbraOption Option, int GroupIndex, int OptionIndex)? FindOptionLocked(
        string groupName, string optionName)
    {
        if (_mod is null)
            return null;

        for (var gi = 0; gi < _mod.Groups.Count; gi++)
        {
            var group = _mod.Groups[gi];
            if (!string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
                continue;
            for (var oi = 0; oi < group.Options.Count; oi++)
            {
                if (string.Equals(group.Options[oi].Name, optionName, StringComparison.OrdinalIgnoreCase))
                    return (group, group.Options[oi], gi, oi);
            }
        }

        return null;
    }

    /// <summary>Loads a mod JSON file for modification, backing it up first (an absent file is recorded as created).</summary>
    private (JsonObject Root, string Path) LoadModJsonLocked(string fileName)
    {
        var path = Path.Combine(_mod!.Directory, fileName);
        BackupLocked(fileName, path);
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new PenumbraLinkException($"{fileName} is not a JSON object.")
            : new JsonObject();
        return (root, path);
    }

    private static void SaveModJson(string path, JsonObject root) =>
        File.WriteAllText(path, root.ToJsonString(WriteOptions));

    /// <summary>Finds a Single/Multi group node by name in meta.json's Groups array (never an Imc group).</summary>
    private static JsonObject FindGroupNode(JsonObject metaRoot, string groupName)
    {
        if (metaRoot["Groups"] is JsonArray groups)
        {
            foreach (var node in groups)
            {
                if (node is JsonObject group
                    && (string?)group["Type"] is "Single" or "Multi"
                    && string.Equals((string?)group["Name"], groupName, StringComparison.OrdinalIgnoreCase))
                    return group;
            }
        }

        throw new PenumbraLinkException($"Group “{groupName}” not found in the mod JSON.");
    }

    private static JsonObject FindOptionNode(JsonObject optionParent, string optionName)
    {
        if (optionParent["Options"] is JsonArray options)
        {
            foreach (var node in options)
            {
                if (node is JsonObject option
                    && string.Equals((string?)option["Name"], optionName, StringComparison.OrdinalIgnoreCase))
                    return option;
            }
        }

        throw new PenumbraLinkException($"Option “{optionName}” not found in the mod JSON.");
    }

    private string NextGroupFileNameLocked(string groupName)
    {
        var next = 1;
        foreach (var file in Directory.GetFiles(_mod!.Directory, "group_*.json"))
        {
            var match = Regex.Match(Path.GetFileName(file), @"^group_(\d+)_");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n >= next)
                next = n + 1;
        }

        return $"group_{next:D3}_{SanitizePathSegment(groupName)}.json";
    }

    /// <summary>Lowercased alphanumeric-and-underscore form of a name, safe as a file-system path segment.</summary>
    private static string SanitizePathSegment(string name)
    {
        var safe = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' ? c : '_')
            .ToArray());
        return safe.Trim('_').Length == 0 ? "x" : safe;
    }

    private string ResolveModPathLocked(string rel)
    {
        var root = Path.GetFullPath(_mod!.Directory);
        var full = Path.GetFullPath(Path.Combine(root, ToPlatformPath(rel)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PenumbraLinkException($"The mod redirects \"{rel}\" outside its own folder — refusing to touch it.");

        // Mod JSON casing routinely differs from the on-disk names (Penumbra
        // runs on case-insensitive filesystems); resolve to the real file.
        return ModPaths.ResolveCaseInsensitive(root, rel);
    }

    private static string ToPlatformPath(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Removes now-empty directories a deleted created-file leaves behind, up to (but never including) the mod root.</summary>
    private static void PruneEmptyDirectories(string root, string startDir)
    {
        var dir = Path.GetFullPath(startDir);
        while (!string.Equals(dir, root, StringComparison.Ordinal)
               && dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    // --- Serialized shapes ---

    private sealed class MetaJson
    {
        public string? Name { get; set; }

        public FilesContainerJson? DefaultData { get; set; }

        public List<GroupJson>? Groups { get; set; }
    }

    private sealed class FilesContainerJson
    {
        public Dictionary<string, string>? Files { get; set; }
    }

    private sealed class GroupJson
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public int Priority { get; set; }

        public long DefaultSettings { get; set; }

        public List<OptionJson>? Options { get; set; }
    }

    private sealed class OptionJson
    {
        public string? Name { get; set; }

        public int Priority { get; set; }

        public Dictionary<string, string>? Files { get; set; }
    }
}
