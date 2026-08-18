using System.Text.Json.Nodes;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Upgrade;

public sealed class DawntrailUpgradeReport
{
    public string ModName = "";
    public int MaterialsUpgraded;
    public int MasksConverted;
    public int NormalsConverted;
    public int IndexTexturesCreated;
    public int AlreadyCurrent;
    public int OtherShaders;
    public List<string> Warnings = [];

    /// <summary>Where the upgraded .pmp was written (modpack flow only).</summary>
    public string? OutputPath;

    public bool AnyChanges => MaterialsUpgraded > 0;

    public string Summary()
    {
        if (!AnyChanges)
            return AlreadyCurrent > 0
                ? $"Nothing to upgrade — {AlreadyCurrent} material{(AlreadyCurrent == 1 ? " is" : "s are")} already Dawntrail-ready."
                : "Nothing to upgrade — the mod has no legacy gear materials.";

        var parts = new List<string>
        {
            $"{MaterialsUpgraded} material{(MaterialsUpgraded == 1 ? "" : "s")} upgraded",
        };
        if (IndexTexturesCreated > 0)
            parts.Add($"{IndexTexturesCreated} index texture{(IndexTexturesCreated == 1 ? "" : "s")} created");
        if (MasksConverted > 0)
            parts.Add($"{MasksConverted} mask{(MasksConverted == 1 ? "" : "s")} converted");
        if (NormalsConverted > 0)
            parts.Add($"{NormalsConverted} normal map{(NormalsConverted == 1 ? "" : "s")} converted");
        if (AlreadyCurrent > 0)
            parts.Add($"{AlreadyCurrent} already current");
        if (OtherShaders > 0)
            parts.Add($"{OtherShaders} non-gear material{(OtherShaders == 1 ? "" : "s")} untouched");
        return string.Join(" · ", parts) + ".";
    }
}

/// <summary>
/// Upgrades an installed Penumbra mod to Dawntrail:
/// every legacy gear material the mod redirects (in the default files or any
/// option) is converted (see <see cref="DawntrailUpgrade"/>), index textures
/// are generated from the legacy normals, and masks/normals get their
/// channels moved. Everything modified or created goes through the shared
/// .moonlace-backup store, so the upgrade is fully revertible from the
/// Penumbra menu. Skin/hair/iris and already-current materials are left
/// alone.
/// </summary>
public sealed class DawntrailModUpgrader
{
    private readonly IPenumbraLinkService _link;
    private readonly LuminaGameDataService _gameData;
    private readonly ILogger<DawntrailModUpgrader> _logger;

    public DawntrailModUpgrader(
        IPenumbraLinkService link, LuminaGameDataService gameData, ILogger<DawntrailModUpgrader> logger)
    {
        _link = link;
        _gameData = gameData;
        _logger = logger;
    }

    /// <summary>One file container of the mod: the default files or a single option.</summary>
    private sealed record Container(
        string Label, IReadOnlyDictionary<string, string> Files,
        string JsonFile, string? GroupName, string? OptionName);

    /// <summary>Upgrades an installed mod folder in place (revertible via the shared backup store).</summary>
    public Task<DawntrailUpgradeReport> UpgradeAsync(string modDirectory, CancellationToken ct = default) =>
        Task.Run(() => UpgradeFolder(modDirectory, ct), ct);

    /// <summary>
    /// Upgrades a distributable modpack (.pmp / .ttmp2 / .ttmp) into a new
    /// upgraded .pmp at <paramref name="outputPmpPath"/>. The input file is
    /// never modified; the work happens in a temporary folder.
    /// </summary>
    public Task<DawntrailUpgradeReport> UpgradeModpackAsync(
        string modpackPath, string outputPmpPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "moonlace-upgrade-" + Guid.NewGuid().ToString("N"));
            try
            {
                var warnings = new List<string>();
                ModpackFile.ExtractToFolder(modpackPath, tempDir, warnings);
                var report = UpgradeFolder(tempDir, ct);
                report.Warnings.InsertRange(0, warnings);
                ModpackFile.PackagePmp(tempDir, outputPmpPath);
                report.OutputPath = outputPmpPath;
                _logger.LogInformation("Packaged upgraded modpack to {Out}", outputPmpPath);
                return report;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove the temporary upgrade folder {Dir}", tempDir);
                }
            }
        }, ct);
    }

    private DawntrailUpgradeReport UpgradeFolder(string modDirectory, CancellationToken ct)
    {
        {
            var report = new DawntrailUpgradeReport();
            var info = _link.Inspect(modDirectory);
            report.ModName = info.Name;
            var root = Path.GetFullPath(info.Directory);
            var singleFile = IsSingleFileLayout(root);
            var backups = ModBackups.Load(root);

            var containers = new List<Container>
            {
                new("Default files", info.DefaultFiles, singleFile ? "meta.json" : "default_mod.json", null, null),
            };
            foreach (var group in info.Groups)
            {
                foreach (var option in group.Options)
                    containers.Add(new Container($"{group.Name} / {option.Name}", option.Files,
                        singleFile ? "meta.json" : group.SourceFile ?? "meta.json", group.Name, option.Name));
            }

            // Any-container map for finding the mod file behind a referenced texture.
            var combined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in containers)
            {
                foreach (var (gamePath, rel) in container.Files)
                    combined.TryAdd(gamePath, rel);
            }

            string? Resolve(string rel)
            {
                var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return null;
                return ModPaths.ResolveCaseInsensitive(root, rel);
            }

            void Backup(string rel)
            {
                var full = Resolve(rel);
                if (full is not null)
                    ModBackups.EnsureBackedUp(root, backups, rel, full);
            }

            var upgradedMtrls = new Dictionary<string, DawntrailUpgrade.MaterialResult>(StringComparer.OrdinalIgnoreCase);
            var convertedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var container in containers)
            {
                foreach (var (gamePath, rel) in container.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Penumbra keeps unused files under pseudo-keys the game
                    // never requests — they are not active content.
                    if (!ModPaths.LooksLikeGamePath(gamePath))
                        continue;

                    var mtrlFile = Resolve(rel);
                    if (mtrlFile is null || !File.Exists(mtrlFile))
                    {
                        report.Warnings.Add($"Missing mod file for {gamePath} ({rel}).");
                        continue;
                    }

                    if (upgradedMtrls.TryGetValue(rel, out var previous))
                    {
                        // File already upgraded through another container; only
                        // the index redirection may still be missing here.
                        RegisterIndexIfNeeded(root, singleFile, container, previous, backups, report);
                        continue;
                    }

                    MtrlDocument doc;
                    try
                    {
                        doc = MtrlDocument.Parse(File.ReadAllBytes(mtrlFile));
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"Could not parse {rel}: {ex.Message}");
                        continue;
                    }

                    if (!DawntrailUpgrade.IsLegacyCharacterMaterial(doc))
                    {
                        if (doc.ShaderPack is "characterlegacy.shpk" or "characterglass.shpk"
                            || (doc.ShaderPack == "character.shpk" && doc.DataSet.Length >= 2048))
                            report.AlreadyCurrent++;
                        else
                            report.OtherShaders++;
                        continue;
                    }

                    // Normals the mod does not replace were upgraded by the game
                    // itself — point at the game's own index texture then.
                    string? vanillaIndex = null;
                    var parsedNormal = FindNormalPath(doc);
                    if (parsedNormal is not null && !combined.ContainsKey(parsedNormal))
                        vanillaIndex = FindVanillaIndexPath(gamePath);

                    var result = DawntrailUpgrade.UpgradeCharacterMaterial(File.ReadAllBytes(mtrlFile), vanillaIndex);
                    Backup(rel);
                    File.WriteAllBytes(mtrlFile, result.Data);
                    upgradedMtrls[rel] = result;
                    report.MaterialsUpgraded++;
                    _logger.LogInformation("Upgraded material {Rel} ({Path})", rel, gamePath);

                    ConvertMask(result, container, combined, Resolve, Backup, convertedTextures, report);
                    CreateIndexAndFixNormal(root, singleFile, container, result, combined, Resolve, backups,
                        convertedTextures, report);
                }
            }

            _logger.LogInformation("Dawntrail upgrade of \"{Mod}\": {Summary}", info.Name, report.Summary());
            return report;
        }
    }

    private void ConvertMask(
        DawntrailUpgrade.MaterialResult result, Container container,
        Dictionary<string, string> combined, Func<string, string?> resolve, Action<string> backup,
        HashSet<string> convertedTextures, DawntrailUpgradeReport report)
    {
        if (result.MaskPath is null)
            return;

        var rel = container.Files.TryGetValue(result.MaskPath, out var own)
            ? own
            : combined.GetValueOrDefault(result.MaskPath);
        if (rel is null)
        {
            // Vanilla-referenced masks were converted by the game itself.
            return;
        }

        if (!convertedTextures.Add(rel))
            return;

        var file = resolve(rel);
        var decoded = file is null ? null : DecodeTexture(file);
        if (decoded is null)
        {
            report.Warnings.Add($"Could not decode mask texture {rel}; convert it manually.");
            return;
        }

        DawntrailUpgrade.ConvertLegacyMaskRgba(decoded.Value.Rgba);
        backup(rel);
        File.WriteAllBytes(file!, TexWriter.Write(decoded.Value.Width, decoded.Value.Height, decoded.Value.Rgba));
        report.MasksConverted++;
        _logger.LogInformation("Converted mask {Rel}", rel);
    }

    private void CreateIndexAndFixNormal(
        string root, bool singleFile, Container container, DawntrailUpgrade.MaterialResult result,
        Dictionary<string, string> combined, Func<string, string?> resolve, List<ModBackupEntry> backups,
        HashSet<string> convertedTextures, DawntrailUpgradeReport report)
    {
        if (result.NormalPath is null || result.IndexPath is null)
            return;

        var normalRel = container.Files.TryGetValue(result.NormalPath, out var own)
            ? own
            : combined.GetValueOrDefault(result.NormalPath);
        if (normalRel is null)
        {
            // The material used a vanilla normal; the vanilla index path was
            // substituted during the material upgrade when one exists.
            if (!_gameData.IsInitialized || !_gameData.Lumina.FileExists(result.IndexPath))
                report.Warnings.Add(
                    $"{result.NormalPath} is not part of the mod and no game index texture was found — " +
                    "the upgraded material may need a manually assigned index texture.");
            return;
        }

        var normalFile = resolve(normalRel);
        var decoded = normalFile is null ? null : DecodeTexture(normalFile);
        if (decoded is null)
        {
            report.Warnings.Add($"Could not decode normal texture {normalRel}; no index texture was generated.");
            return;
        }

        var indexRel = DawntrailUpgrade.DeriveIndexPath(normalRel);
        var indexFile = resolve(indexRel);
        if (indexFile is null)
            return;

        if (convertedTextures.Add(indexRel) && !File.Exists(indexFile))
        {
            var indexRgba = DawntrailUpgrade.CreateIndexRgba(decoded.Value.Rgba);
            ModBackups.EnsureBackedUp(root, backups, indexRel, indexFile);
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
            File.WriteAllBytes(indexFile, TexWriter.Write(decoded.Value.Width, decoded.Value.Height, indexRgba));
            report.IndexTexturesCreated++;
            _logger.LogInformation("Created index texture {Rel}", indexRel);
        }

        if (convertedTextures.Add(normalRel))
        {
            // Move opacity from blue to alpha now that the row data lives in the index map.
            DawntrailUpgrade.ConvertLegacyNormalRgba(decoded.Value.Rgba);
            ModBackups.EnsureBackedUp(root, backups, normalRel, normalFile!);
            File.WriteAllBytes(normalFile!, TexWriter.Write(decoded.Value.Width, decoded.Value.Height, decoded.Value.Rgba));
            report.NormalsConverted++;
        }

        RegisterFile(root, singleFile, container, result.IndexPath, indexRel, backups);
    }

    private void RegisterIndexIfNeeded(
        string root, bool singleFile, Container container, DawntrailUpgrade.MaterialResult result,
        List<ModBackupEntry> backups, DawntrailUpgradeReport report)
    {
        if (result.IndexPath is null)
            return;
        if (container.Files.ContainsKey(result.IndexPath))
            return;

        var normalRel = container.Files.GetValueOrDefault(result.NormalPath ?? "");
        if (normalRel is null)
            return;

        RegisterFile(root, singleFile, container, result.IndexPath, DawntrailUpgrade.DeriveIndexPath(normalRel), backups);
    }

    /// <summary>Adds a game-path → file redirection to a container's JSON (backed up first).</summary>
    private void RegisterFile(
        string root, bool singleFile, Container container, string gamePath, string rel, List<ModBackupEntry> backups)
    {
        if (container.Files.ContainsKey(gamePath))
            return;

        var jsonPath = Path.Combine(root, container.JsonFile);
        ModBackups.EnsureBackedUp(root, backups, container.JsonFile, jsonPath);
        if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject json)
            return;

        JsonObject filesParent;
        if (container.GroupName is null)
        {
            filesParent = singleFile
                ? json["DefaultData"] as JsonObject ?? (JsonObject)(json["DefaultData"] = new JsonObject())
                : json;
        }
        else
        {
            var groupNode = singleFile ? FindGroupNode(json, container.GroupName) : json;
            filesParent = FindOptionNode(groupNode, container.OptionName!);
        }

        if (filesParent["Files"] is not JsonObject files)
            filesParent["Files"] = files = new JsonObject();
        files[gamePath] = rel.Replace('/', '\\');
        File.WriteAllText(jsonPath, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _logger.LogInformation("Registered {Path} -> {Rel} in {Container}", gamePath, rel, container.Label);
    }

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

    private static bool IsSingleFileLayout(string root)
    {
        var meta = Path.Combine(root, "meta.json");
        if (!File.Exists(meta))
            return false;
        return JsonNode.Parse(File.ReadAllText(meta)) is JsonObject json
            && (json["DefaultData"] is not null || json["Groups"] is not null);
    }

    private static string? FindNormalPath(MtrlDocument doc)
    {
        var sampler = doc.Samplers.FirstOrDefault(s => s.SamplerId == DawntrailUpgrade.SamplerNormalId);
        return sampler is null || sampler.TextureIndex >= doc.Textures.Count
            ? null
            : doc.Textures[sampler.TextureIndex].Path;
    }

    /// <summary>The game's own index texture path for a vanilla material game path, when it exists.</summary>
    private string? FindVanillaIndexPath(string mtrlGamePath)
    {
        try
        {
            if (!_gameData.IsInitialized || !_gameData.Lumina.FileExists(mtrlGamePath))
                return null;
            var vanilla = MtrlDocument.Parse(_gameData.Lumina.GetFile(mtrlGamePath)!.Data);
            var sampler = vanilla.Samplers.FirstOrDefault(s => s.SamplerId == DawntrailUpgrade.SamplerIndexId);
            return sampler is null || sampler.TextureIndex >= vanilla.Textures.Count
                ? null
                : vanilla.Textures[sampler.TextureIndex].Path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vanilla index lookup failed for {Path}", mtrlGamePath);
            return null;
        }
    }

    /// <summary>Decodes a mod .tex file to RGBA8: Moonlace's own uncompressed format directly, anything else via Lumina.</summary>
    private (int Width, int Height, byte[] Rgba)? DecodeTexture(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (TexWriter.TryReadB8G8R8A8(bytes) is { } direct)
            {
                var rgba = direct.Rgba;
                return (direct.Width, direct.Height, rgba);
            }

            if (!_gameData.IsInitialized)
                return null;

            var tex = _gameData.Lumina.GetFileFromDisk<TexFile>(file);
            var converted = tex.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8);
            var pixels = new byte[converted.RawData.Length];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = converted.RawData[i + 2];
                pixels[i + 1] = converted.RawData[i + 1];
                pixels[i + 2] = converted.RawData[i];
                pixels[i + 3] = converted.RawData[i + 3];
            }

            return (converted.Width, converted.Height, pixels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode texture {File}", file);
            return null;
        }
    }
}
