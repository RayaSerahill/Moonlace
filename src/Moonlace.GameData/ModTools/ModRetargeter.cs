using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.Export;
using Moonlace.GameData.Import;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;
using Moonlace.GameData.Upgrade;

namespace Moonlace.GameData.ModTools;

public sealed class ModRetargetException(string message) : Exception(message);

/// <summary>
/// One retargetable binding found in a modpack: every file belonging to one
/// equipment/accessory model version (set code + race/gender + slot).
/// </summary>
public sealed class ModBinding
{
    /// <summary>"e0387" for gear, "a0053" for accessories.</summary>
    public required string SetCode { get; init; }

    /// <summary>Race/gender code the files are modeled for, e.g. "0101".</summary>
    public required string RaceCode { get; init; }

    public required string RaceLabel { get; init; }

    public required EquipSlot Slot { get; init; }

    /// <summary>Slot suffix in the file names, e.g. "top".</summary>
    public required string SlotSuffix { get; init; }

    /// <summary>Names of the items that use this model set and slot.</summary>
    public required IReadOnlyList<string> ItemNames { get; init; }

    /// <summary>The binding's game paths (lowercased), sorted.</summary>
    public required IReadOnlyList<string> GamePaths { get; init; }

    internal IReadOnlyList<EquipmentItem> MatchedItems { get; init; } = [];

    public bool IsAccessory => SetCode.StartsWith('a');

    public string ItemsLabel => ItemNames.Count switch
    {
        0 => $"Unknown item ({SetCode})",
        1 => ItemNames[0],
        _ => $"{ItemNames[0]} (+{ItemNames.Count - 1} more)",
    };

    public string Label =>
        $"{ItemsLabel} — {Slot}, {RaceLabel} · {GamePaths.Count} file{(GamePaths.Count == 1 ? "" : "s")}";
}

public sealed class ModRetargetAnalysis
{
    public required string ModName { get; init; }

    public required IReadOnlyList<ModBinding> Bindings { get; init; }

    /// <summary>Files that are not retargetable gear assets and would be carried unchanged.</summary>
    public required int CarriedFileCount { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed class ModRetargetReport
{
    public string ModName = "";

    public string SourceLabel = "";

    public string DestinationLabel = "";

    public int FilesRewired;

    public int MaterialsPulledIn;

    public int FilesCarried;

    public List<string> Warnings = [];

    public string Summary()
    {
        var parts = new List<string>
        {
            $"{FilesRewired} file{(FilesRewired == 1 ? "" : "s")} rewired from {SourceLabel} to {DestinationLabel}",
        };
        if (MaterialsPulledIn > 0)
            parts.Add($"{MaterialsPulledIn} game material{(MaterialsPulledIn == 1 ? "" : "s")} pulled in to complete the model");
        if (FilesCarried > 0)
            parts.Add($"{FilesCarried} other file{(FilesCarried == 1 ? "" : "s")} carried unchanged");
        return string.Join(" · ", parts) + ".";
    }
}

/// <summary>
/// Retargets a distributable modpack (.pmp / .ttmp2 / .ttmp) onto a different
/// item and/or race/gender: redirect paths are remapped onto the destination's
/// asset paths, the model's material names are re-coded in place, materials
/// get their texture references rewritten, skin materials are repointed to the
/// destination race (gender-base fallback), and item materials the model
/// references but the mod does not ship are pulled from the game data so the
/// result is complete. The output is a new standalone .pmp — the input
/// modpack and the FFXIV installation are never modified.
/// </summary>
public sealed partial class ModRetargeter
{
    private readonly LuminaGameDataService _gameData;
    private readonly AssetPathResolver _resolver;
    private readonly IItemRepository _items;
    private readonly IPenumbraLinkService _link;
    private readonly ILogger<ModRetargeter> _logger;

    public ModRetargeter(
        LuminaGameDataService gameData,
        AssetPathResolver resolver,
        IItemRepository items,
        IPenumbraLinkService link,
        ILogger<ModRetargeter> logger)
    {
        _gameData = gameData;
        _resolver = resolver;
        _items = items;
        _link = link;
        _logger = logger;
    }

    /// <summary>What the modpack's effective files (default option selection) bind to.</summary>
    public async Task<ModRetargetAnalysis> AnalyzeAsync(string modpackPath, CancellationToken ct = default)
    {
        var items = await _items.GetEquipmentItemsAsync(ct);
        return await Task.Run(() => Analyze(modpackPath, items, ct), ct);
    }

    private ModRetargetAnalysis Analyze(string modpackPath, IReadOnlyList<EquipmentItem> items, CancellationToken ct)
    {
        var notes = new List<string>();
        return RunExtracted(modpackPath, notes, (info, effective, root) =>
        {
            var groups = new Dictionary<(string Set, string Race, EquipSlot Slot), List<string>>();
            var tokenRegexes = new Dictionary<string, Regex>(StringComparer.Ordinal);
            var carried = 0;
            var weaponFiles = 0;

            foreach (var rawPath in effective.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (!ModPaths.LooksLikeGamePath(rawPath))
                    continue; // Penumbra pseudo-key for an unused file — not effective content

                var path = rawPath.ToLowerInvariant();
                var setMatch = SetDirRegex().Match(path);
                if (setMatch.Success)
                {
                    var set = setMatch.Groups[2].Value;
                    if (!tokenRegexes.TryGetValue(set, out var tokenRegex))
                        tokenRegexes[set] = tokenRegex = new Regex($@"c(\d{{4}}){Regex.Escape(set)}_([a-z]{{3}})");

                    var token = tokenRegex.Match(path);
                    var slot = token.Success ? AssetPathResolver.SlotFromSuffix(token.Groups[2].Value) : null;
                    if (slot is not null)
                    {
                        var key = (set, token.Groups[1].Value, slot.Value);
                        if (!groups.TryGetValue(key, out var list))
                            groups[key] = list = [];
                        list.Add(path);
                        continue;
                    }
                }

                carried++;
                if (path.StartsWith("chara/weapon/", StringComparison.Ordinal))
                    weaponFiles++;
            }

            var bindings = groups
                .OrderBy(g => g.Key.Set, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Race, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Slot)
                .Select(g =>
                {
                    var matched = items
                        .Where(i => !i.IsWeapon && !i.IsBodyPart && i.Slot == g.Key.Slot
                            && AssetPathResolver.SetCode(i) == g.Key.Set)
                        .ToArray();
                    return new ModBinding
                    {
                        SetCode = g.Key.Set,
                        RaceCode = g.Key.Race,
                        RaceLabel = RaceLabelOf(g.Key.Race),
                        Slot = g.Key.Slot,
                        SlotSuffix = AssetPathResolver.SlotSuffix(g.Key.Slot),
                        ItemNames = matched.Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray(),
                        GamePaths = g.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        MatchedItems = matched,
                    };
                })
                .ToArray();

            if (weaponFiles > 0)
                notes.Add($"{weaponFiles} weapon file{(weaponFiles == 1 ? "" : "s")} — weapons cannot be retargeted and are carried unchanged.");
            if (carried > weaponFiles)
                notes.Add($"{carried - weaponFiles} file{(carried - weaponFiles == 1 ? " is" : "s are")} not race-coded gear assets (skin, IMC, VFX, …) and would be carried unchanged.");

            _logger.LogInformation("Analyzed modpack \"{Mod}\": {Bindings} retargetable binding(s), {Carried} carried",
                info.Name, bindings.Length, carried);
            return new ModRetargetAnalysis
            {
                ModName = info.Name,
                Bindings = bindings,
                CarriedFileCount = carried,
                Notes = notes,
            };
        });
    }

    /// <summary>
    /// Rewires one binding of the modpack onto <paramref name="destination"/> +
    /// <paramref name="destRaceCode"/> and writes the result as a new .pmp at
    /// <paramref name="outputPath"/>. Files outside the binding are carried
    /// through unchanged.
    /// </summary>
    public Task<ModRetargetReport> RetargetAsync(
        string modpackPath,
        ModBinding binding,
        EquipmentItem destination,
        string destRaceCode,
        string outputPath,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (destination.IsWeapon || destination.IsBodyPart)
                throw new ModRetargetException("Only equipment and accessory items can be retarget destinations.");
            if (destination.Slot != binding.Slot)
                throw new ModRetargetException($"The destination must use the same equip slot as the modded model ({binding.Slot}).");
            if (destRaceCode.Length != 4 || !destRaceCode.All(char.IsAsciiDigit))
                throw new ModRetargetException($"\"{destRaceCode}\" is not a race/gender code (e.g. 0801).");

            var dstSet = AssetPathResolver.SetCode(destination);
            if (dstSet == binding.SetCode && destRaceCode == binding.RaceCode)
                throw new ModRetargetException("The mod already targets that item and race — nothing to change.");

            var report = new ModRetargetReport
            {
                SourceLabel = $"{binding.ItemsLabel} ({binding.RaceLabel})",
                DestinationLabel = $"{destination.Name} ({RaceLabelOf(destRaceCode)})",
            };

            return RunExtracted(modpackPath, report.Warnings, (info, effective, root) =>
            {
                report.ModName = info.Name;

                var srcKind = binding.IsAccessory ? "accessory" : "equipment";
                var dstKind = destination.IsAccessory ? "accessory" : "equipment";
                var srcDir = $"chara/{srcKind}/{binding.SetCode}/";
                var dstDir = $"chara/{dstKind}/{dstSet}/";
                var srcToken = $"c{binding.RaceCode}{binding.SetCode}";
                var dstToken = $"c{destRaceCode}{dstSet}";
                var destMaterialSet = DestinationMaterialSet(destination);

                // Where each of the binding's files lands on the destination's paths.
                var pathMap = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in binding.GamePaths)
                {
                    var mapped = path.StartsWith(srcDir, StringComparison.Ordinal)
                        ? dstDir + path[srcDir.Length..]
                        : path;
                    mapped = mapped.Replace(srcToken, dstToken, StringComparison.Ordinal);
                    if (path.EndsWith(".mtrl", StringComparison.Ordinal))
                        mapped = MaterialVersionDirRegex().Replace(mapped, $"/material/v{destMaterialSet:D4}/");
                    pathMap[path] = mapped;
                }

                var context = new RetargetContext(
                    SrcToken: srcToken,
                    DstToken: dstToken,
                    SrcRace: binding.RaceCode,
                    DstRace: destRaceCode,
                    SrcResolved: new ResolvedModelInfo(
                        $"chara/{srcKind}/{binding.SetCode}/model/{srcToken}_{binding.SlotSuffix}.mdl",
                        $"chara/{srcKind}/{binding.SetCode}/material",
                        SourceMaterialSet(binding)),
                    DstResolved: new ResolvedModelInfo(
                        $"chara/{dstKind}/{dstSet}/model/{dstToken}_{binding.SlotSuffix}.mdl",
                        $"chara/{dstKind}/{dstSet}/material",
                        destMaterialSet),
                    PathMap: pathMap,
                    ProvidedMaterialNames: new HashSet<string>(
                        binding.GamePaths
                            .Where(p => p.EndsWith(".mtrl", StringComparison.Ordinal))
                            .Select(p => "/" + Path.GetFileName(p)),
                        StringComparer.Ordinal));

                var bindingPaths = new HashSet<string>(binding.GamePaths, StringComparer.Ordinal);
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var pulled = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                // Carried files of the same set fall in two groups: other
                // model versions (their own bindings — no warning needed) and
                // genuinely shared files without a race token.
                var setTokenRegex = new Regex($@"c\d{{4}}{Regex.Escape(binding.SetCode)}_");
                var setSharedCarried = 0;

                foreach (var (rawPath, rel) in effective.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ModPaths.LooksLikeGamePath(rawPath))
                        continue;

                    var path = rawPath.ToLowerInvariant();
                    var bytes = ReadModFile(root, rel, path, report.Warnings);
                    if (bytes is null)
                        continue;

                    if (bindingPaths.Contains(path))
                    {
                        var mapped = pathMap[path];
                        bytes = Path.GetExtension(path) switch
                        {
                            ".mdl" => RetargetModel(bytes, context, pulled, report.Warnings),
                            ".mtrl" => RewriteMaterialTextures(bytes, path, pathMap, report.Warnings),
                            _ => bytes,
                        };
                        if (files.ContainsKey(mapped))
                            report.Warnings.Add($"Two mod files map onto {mapped}; the later one wins.");
                        files[mapped] = bytes;
                        report.FilesRewired++;
                    }
                    else
                    {
                        if (!files.TryAdd(path, bytes))
                            report.Warnings.Add($"A retargeted file replaces the mod's own {path}.");
                        report.FilesCarried++;
                        if (path.StartsWith(srcDir, StringComparison.Ordinal) && !setTokenRegex.IsMatch(path))
                            setSharedCarried++;
                    }
                }

                foreach (var (path, bytes) in pulled)
                {
                    if (files.TryAdd(path, bytes))
                        report.MaterialsPulledIn++;
                }

                if (setSharedCarried > 0)
                    report.Warnings.Add(
                        $"{setSharedCarried} file{(setSharedCarried == 1 ? "" : "s")} under {binding.SetCode} " +
                        "carry no race code (IMC, VFX, …) — kept unchanged, still affecting the original item.");
                if (report.FilesRewired == 0)
                    throw new ModRetargetException("None of the mod's files matched the selected model — nothing to retarget.");

                PmpExporter.Export(files, new PmpMetadata
                {
                    Name = $"{info.Name} → {destination.Name}",
                    Description = $"Retargeted by Moonlace from {report.SourceLabel} to {report.DestinationLabel}.",
                }, outputPath);

                _logger.LogInformation("Retargeted \"{Mod}\": {Summary} → {Out}", info.Name, report.Summary(), outputPath);
                return report;
            });
        }, ct);
    }

    private sealed record RetargetContext(
        string SrcToken,
        string DstToken,
        string SrcRace,
        string DstRace,
        ResolvedModelInfo SrcResolved,
        ResolvedModelInfo DstResolved,
        IReadOnlyDictionary<string, string> PathMap,
        HashSet<string> ProvidedMaterialNames);

    /// <summary>
    /// Re-codes the model's material names in place: item materials get the
    /// destination token, skin/character materials are repointed to the
    /// destination race (or its gender base). Item materials the mod does not
    /// ship are pulled from the game so the destination paths stay complete.
    /// </summary>
    private byte[] RetargetModel(byte[] mdl, RetargetContext ctx, Dictionary<string, byte[]> pulled, List<string> warnings)
    {
        var parsed = MdlParser.Parse(mdl);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in parsed.MaterialNames.Distinct(StringComparer.Ordinal))
        {
            if (!name.StartsWith('/'))
            {
                // Absolute path: only remap it when the mod redirects that
                // exact path (the mapping keeps the byte length); other
                // absolute references stay valid as they are.
                if (ctx.PathMap.TryGetValue(name.ToLowerInvariant(), out var mappedAbs) && mappedAbs.Length == name.Length)
                    renames[name] = mappedAbs;
                continue;
            }

            if (name.Contains(ctx.SrcToken, StringComparison.Ordinal))
            {
                var renamed = name.Replace(ctx.SrcToken, ctx.DstToken, StringComparison.Ordinal);
                renames[name] = renamed;
                if (!ctx.ProvidedMaterialNames.Contains(name))
                    PullGameMaterial(name, renamed, ctx, pulled, warnings);
                continue;
            }

            // Character material (skin, hair, …): repoint to the closest race
            // the game ships materials for — skin only exists for the base
            // bodies (e.g. Miqo'te ♀ uses the c0201 skin).
            var human = BodyMaterialRegex().Match(name);
            if (!human.Success || ctx.DstRace == ctx.SrcRace)
                continue;
            var nameRace = human.Groups[1].Value;
            if (nameRace != ctx.SrcRace && nameRace != AssetPathResolver.GenderBaseRace(ctx.SrcRace))
                continue;

            var alreadyRight = false;
            string? chosen = null;
            foreach (var candidate in new[] { ctx.DstRace, AssetPathResolver.GenderBaseRace(ctx.DstRace) }.Distinct())
            {
                if (candidate == nameRace)
                {
                    alreadyRight = true;
                    break;
                }

                var candidateName = name.Replace($"c{nameRace}", $"c{candidate}", StringComparison.Ordinal);
                if (_gameData.Lumina.FileExists(_resolver.ResolveMaterialPath(ctx.DstResolved, candidateName)))
                {
                    chosen = candidateName;
                    break;
                }
            }

            if (alreadyRight)
                continue;
            if (chosen is not null)
                renames[name] = chosen;
            else
                warnings.Add($"No c{ctx.DstRace} equivalent for {name} — kept as-is.");
        }

        return renames.Count == 0 ? mdl : MdlMaterialRenamer.RenameMaterials(mdl, n => renames.GetValueOrDefault(n));
    }

    /// <summary>Copies an item material the mod does not ship from the game onto the destination's paths.</summary>
    private void PullGameMaterial(string name, string renamed, RetargetContext ctx, Dictionary<string, byte[]> pulled, List<string> warnings)
    {
        var sourcePath = _resolver.ResolveMaterialPath(ctx.SrcResolved, name);
        var bytes = _gameData.Lumina.FileExists(sourcePath) ? _gameData.Lumina.GetFile(sourcePath)?.Data : null;
        if (bytes is null)
        {
            warnings.Add($"The model references {name}, which is neither in the mod nor the game — it may render untextured.");
            return;
        }

        // The game material's textures that the mod redirects follow the
        // retarget; everything else keeps its (valid) original game path.
        bytes = RewriteMaterialTextures(bytes, sourcePath, ctx.PathMap, warnings);
        pulled[_resolver.ResolveMaterialPath(ctx.DstResolved, renamed)] = bytes;
    }

    /// <summary>Repoints the material's texture references that the retarget moved; other references stay.</summary>
    private static byte[] RewriteMaterialTextures(byte[] mtrl, string mtrlPath, IReadOnlyDictionary<string, string> pathMap, List<string> warnings)
    {
        var parsed = MtrlParser.Parse(mtrl);
        var newPaths = parsed.TexturePaths
            .Select(p => string.IsNullOrEmpty(p) ? p : pathMap.GetValueOrDefault(p.ToLowerInvariant(), p))
            .ToArray();
        if (newPaths.SequenceEqual(parsed.TexturePaths, StringComparer.Ordinal))
            return mtrl;

        try
        {
            return MtrlWriter.ReplaceTexturePaths(mtrl, newPaths);
        }
        catch (ArgumentException ex)
        {
            warnings.Add($"Texture paths in {mtrlPath} could not be rewritten ({ex.Message}) — kept as-is.");
            return mtrl;
        }
    }

    private int SourceMaterialSet(ModBinding binding)
    {
        // The mod's own material folder is the best answer; fall back to a
        // matched item's IMC lookup, then to set 1.
        foreach (var path in binding.GamePaths)
        {
            if (!path.EndsWith(".mtrl", StringComparison.Ordinal))
                continue;
            var match = MaterialVersionDirRegex().Match(path);
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }

        if (binding.MatchedItems.Count > 0)
        {
            try
            {
                return _resolver.Resolve(binding.MatchedItems[0]).MaterialSet;
            }
            catch (AssetNotFoundException)
            {
            }
        }

        return 1;
    }

    private int DestinationMaterialSet(EquipmentItem destination)
    {
        try
        {
            return _resolver.Resolve(destination).MaterialSet;
        }
        catch (AssetNotFoundException)
        {
            return Math.Max(1, (int)destination.Variant);
        }
    }

    private static string RaceLabelOf(string raceCode) =>
        AssetPathResolver.KnownRaces.FirstOrDefault(r => r.Code == raceCode)?.Label ?? $"c{raceCode}";

    private T RunExtracted<T>(
        string modpackPath,
        List<string> warnings,
        Func<PenumbraModInfo, IReadOnlyDictionary<string, string>, string, T> work)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "moonlace-retarget-" + Guid.NewGuid().ToString("N"));
        try
        {
            ModpackFile.ExtractToFolder(modpackPath, tempDir, warnings);
            var info = _link.Inspect(tempDir);
            var effective = ModpackImporter.EffectiveFiles(info);
            return work(info, effective, Path.GetFullPath(info.Directory));
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
                _logger.LogWarning(ex, "Could not remove the temporary retarget folder {Dir}", tempDir);
            }
        }
    }

    private static byte[]? ReadModFile(string root, string rel, string gamePath, List<string> warnings)
    {
        var file = Path.GetFullPath(ModPaths.ResolveCaseInsensitive(root, rel));
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            warnings.Add($"\"{rel}\" points outside the modpack — skipped.");
            return null;
        }

        if (!File.Exists(file))
        {
            warnings.Add($"Missing modpack file for {gamePath} ({rel}).");
            return null;
        }

        return File.ReadAllBytes(file);
    }

    [GeneratedRegex(@"^chara/(equipment|accessory)/([ea]\d{4})/")]
    private static partial Regex SetDirRegex();

    [GeneratedRegex(@"/material/v(\d{4})/")]
    private static partial Regex MaterialVersionDirRegex();

    [GeneratedRegex(@"^/mt_c(\d{4})[bfhtz]\d{4}_")]
    private static partial Regex BodyMaterialRegex();
}
