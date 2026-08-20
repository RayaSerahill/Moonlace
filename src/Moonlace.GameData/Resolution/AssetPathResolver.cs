using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;

namespace Moonlace.GameData.Resolution;

/// <summary>
/// Builds FFXIV asset paths for equipment items: model path (with race-code
/// fallback for gear), IMC-based material set selection, and material paths
/// from model-relative material names.
/// </summary>
public sealed partial class AssetPathResolver
{
    private readonly LuminaGameDataService _gameData;
    private readonly EffectiveAssetProvider _assets;
    private readonly ILogger<AssetPathResolver> _logger;

    /// <summary>
    /// Race/gender model codes for equipment, in probe order. c0101
    /// (Midlander male) first — the fallback body most gear is modeled for.
    /// Shared with the item repository for naming body parts.
    /// </summary>
    internal static readonly (string Code, string Label)[] RaceTable =
    [
        ("0101", "Midlander ♂"),
        ("0201", "Midlander ♀"),
        ("0301", "Highlander ♂"),
        ("0401", "Highlander ♀"),
        ("0501", "Elezen ♂"),
        ("0601", "Elezen ♀"),
        ("0701", "Miqo'te ♂"),
        ("0801", "Miqo'te ♀"),
        ("0901", "Roegadyn ♂"),
        ("1001", "Roegadyn ♀"),
        ("1101", "Lalafell ♂"),
        ("1201", "Lalafell ♀"),
        ("1301", "Au Ra ♂"),
        ("1401", "Au Ra ♀"),
        ("1501", "Hrothgar ♂"),
        ("1601", "Hrothgar ♀"),
        ("1701", "Viera ♂"),
        ("1801", "Viera ♀"),
    ];

    /// <summary>Every known race/gender code with its display label, for destination pickers.</summary>
    public static IReadOnlyList<RaceVariant> KnownRaces { get; } =
        RaceTable.Select(r => new RaceVariant(r.Code, r.Label)).ToArray();

    /// <summary>"0101" for male race codes, "0201" for female — the bodies whose skin materials always exist.</summary>
    internal static string GenderBaseRace(string raceCode) =>
        raceCode.Length == 4 && int.TryParse(raceCode[..2], out var race) && race % 2 == 0 ? "0201" : "0101";

    /// <summary>
    /// The race code (e.g. "0801") equipment resolution should use. Null
    /// falls back to probe order. Set from the UI's model-version selector;
    /// read by every pipeline that resolves this item (viewport, editing,
    /// exports), so edits apply to exactly the selected version.
    /// </summary>
    public volatile string? PreferredRaceCode;

    public AssetPathResolver(LuminaGameDataService gameData, EffectiveAssetProvider assets, ILogger<AssetPathResolver> logger)
    {
        _gameData = gameData;
        _assets = assets;
        _logger = logger;
    }

    /// <summary>Resolves the model path and material set id for an item, or throws with a useful message.</summary>
    public ResolvedModelInfo Resolve(EquipmentItem item)
    {
        if (item.IsWeapon)
            return ResolveWeapon(item);
        if (item.IsBodyPart)
            return ResolveBodyPart(item);
        return ResolveEquipment(item);
    }

    private ResolvedModelInfo ResolveWeapon(EquipmentItem item)
    {
        var w = $"w{item.ModelId:D4}";
        var b = $"b{item.SecondaryId:D4}";
        var mdlPath = $"chara/weapon/{w}/obj/body/{b}/model/{w}{b}.mdl";
        if (!_gameData.Lumina.FileExists(mdlPath))
            throw new AssetNotFoundException($"Weapon model not found: {mdlPath}");

        var imcPath = $"chara/weapon/{w}/obj/body/{b}/{b}.imc";
        var materialSet = LookupMaterialSet(imcPath, partIndex: 0, item.Variant);
        var materialBase = $"chara/weapon/{w}/obj/body/{b}/material";
        return new ResolvedModelInfo(mdlPath, materialBase, materialSet);
    }

    /// <summary>
    /// Model versions (race variants) that exist for this item's
    /// equipment/accessory model — game-shipped ones and versions that exist
    /// only as edits (session copy or linked-mod file).
    /// </summary>
    public IReadOnlyList<RaceVariant> GetAvailableVariants(EquipmentItem item)
    {
        // Weapons have no race variants; a body part's race is its identity.
        if (item.IsWeapon || item.IsBodyPart)
            return [];

        var set = SetCode(item);
        var suffix = SlotSuffix(item.Slot);
        return RaceTable
            .Where(race => _assets.FileExists(EquipmentMdlPath(item, set, race.Code, suffix)))
            .Select(race => new RaceVariant(race.Code, race.Label))
            .ToArray();
    }

    /// <summary>Race/gender combinations this item has no model version for yet — the creatable ones.</summary>
    public IReadOnlyList<RaceVariant> GetMissingVariants(EquipmentItem item)
    {
        if (item.IsWeapon || item.IsBodyPart)
            return [];

        var set = SetCode(item);
        var suffix = SlotSuffix(item.Slot);
        return RaceTable
            .Where(race => !_assets.FileExists(EquipmentMdlPath(item, set, race.Code, suffix)))
            .Select(race => new RaceVariant(race.Code, race.Label))
            .ToArray();
    }

    /// <summary>The equipment/accessory model path a given race code uses, whether or not it exists.</summary>
    public string GetEquipmentModelPath(EquipmentItem item, string raceCode)
    {
        if (item.IsWeapon || item.IsBodyPart)
            throw new ArgumentException("Only equipment and accessories have race-coded model paths.", nameof(item));
        return EquipmentMdlPath(item, SetCode(item), raceCode, SlotSuffix(item.Slot));
    }

    /// <summary>
    /// Resolves an equipment/accessory item for one specific race code,
    /// ignoring <see cref="PreferredRaceCode"/>. Throws when that version
    /// does not exist (not even as an edit).
    /// </summary>
    public ResolvedModelInfo ResolveForRace(EquipmentItem item, string raceCode)
    {
        var mdlPath = GetEquipmentModelPath(item, raceCode);
        if (!_assets.FileExists(mdlPath))
            throw new AssetNotFoundException($"No c{raceCode} model version exists: {mdlPath}");

        var set = SetCode(item);
        var kind = item.IsAccessory ? "accessory" : "equipment";
        var imcPath = $"chara/{kind}/{set}/{set}.imc";
        var materialSet = LookupMaterialSet(imcPath, SlotImcPart(item.Slot), item.Variant);
        return new ResolvedModelInfo(mdlPath, $"chara/{kind}/{set}/material", materialSet);
    }

    /// <summary>"e0119" for gear, "a0053" for accessories.</summary>
    internal static string SetCode(EquipmentItem item) =>
        $"{(item.IsAccessory ? 'a' : 'e')}{item.ModelId:D4}";

    private static string EquipmentMdlPath(EquipmentItem item, string set, string race, string suffix) =>
        $"chara/{(item.IsAccessory ? "accessory" : "equipment")}/{set}/model/c{race}{set}_{suffix}.mdl";

    private ResolvedModelInfo ResolveBodyPart(EquipmentItem item)
    {
        var race = item.RaceCode
            ?? throw new AssetNotFoundException($"Body part \"{item.Name}\" has no race code.");
        var (dir, letter, suffix) = item.Slot switch
        {
            EquipSlot.Face => ("face", 'f', "fac"),
            EquipSlot.Tail => ("tail", 't', "til"),
            EquipSlot.HumanBody => ("body", 'b', "top"),
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };

        var part = $"{letter}{item.ModelId:D4}";
        var partBase = $"chara/human/c{race}/obj/{dir}/{part}";
        var mdlPath = $"{partBase}/model/c{race}{part}_{suffix}.mdl";
        if (!_gameData.Lumina.FileExists(mdlPath))
            throw new AssetNotFoundException($"Body part model not found: {mdlPath}");

        // No IMC for body parts; materials live under the part's own material
        // directory (v0001, or flat for faces — see ResolveMaterialPath).
        return new ResolvedModelInfo(mdlPath, $"{partBase}/material", MaterialSet: 1);
    }

    private ResolvedModelInfo ResolveEquipment(EquipmentItem item)
    {
        var set = SetCode(item);
        var suffix = SlotSuffix(item.Slot);

        string? mdlPath = null;
        var preferred = PreferredRaceCode;
        if (preferred is not null)
        {
            var candidate = EquipmentMdlPath(item, set, preferred, suffix);
            if (_assets.FileExists(candidate))
                mdlPath = candidate;
            else
                _logger.LogWarning("No c{Race} model for {Item}; falling back to probe order", preferred, set);
        }

        if (mdlPath is null)
        {
            foreach (var (race, _) in RaceTable)
            {
                var candidate = EquipmentMdlPath(item, set, race, suffix);
                if (_assets.FileExists(candidate))
                {
                    mdlPath = candidate;
                    break;
                }
            }
        }

        var kind = item.IsAccessory ? "accessory" : "equipment";
        if (mdlPath is null)
            throw new AssetNotFoundException(
                $"Model not found for any known race code: chara/{kind}/{set}/model/c????{set}_{suffix}.mdl");

        var imcPath = $"chara/{kind}/{set}/{set}.imc";
        var materialSet = LookupMaterialSet(imcPath, SlotImcPart(item.Slot), item.Variant);
        var materialBase = $"chara/{kind}/{set}/material";
        return new ResolvedModelInfo(mdlPath, materialBase, materialSet);
    }

    /// <summary>
    /// Looks up the material set id for an IMC variant. Falls back to the raw
    /// variant number when the IMC file is missing or the lookup fails.
    /// </summary>
    private int LookupMaterialSet(string imcPath, int partIndex, int variant)
    {
        try
        {
            var imc = _gameData.Lumina.GetFile<ImcFile>(imcPath);
            if (imc is null)
            {
                _logger.LogWarning("IMC file missing: {Path}; using variant {Variant} as material set", imcPath, variant);
                return variant;
            }

            var entry = imc.GetVariant(partIndex, variant);
            return entry.MaterialId != 0 ? entry.MaterialId : 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IMC lookup failed for {Path} part {Part} variant {Variant}", imcPath, partIndex, variant);
            return variant;
        }
    }

    /// <summary>
    /// Resolves a material name stored in a model (e.g. "/mt_w0201b0001_a.mtrl")
    /// to a full game path. Most names resolve into the item's material set
    /// directory, but equipment models also reference character body-part
    /// materials (skin, hair, …) that live under chara/human.
    /// </summary>
    public string ResolveMaterialPath(ResolvedModelInfo model, string materialName)
    {
        if (!materialName.StartsWith('/'))
            return materialName; // already an absolute game path

        var human = BodyPartMaterialRegex().Match(materialName);
        if (human.Success)
        {
            var race = human.Groups[1].Value;
            var partLetter = human.Groups[2].Value;
            var partId = human.Groups[3].Value;
            var partDir = partLetter switch
            {
                "b" => "body",
                "f" => "face",
                "h" => "hair",
                "t" => "tail",
                "z" => "zear",
                _ => "body",
            };

            // Body/tail materials live in a v0001 folder; Dawntrail face
            // materials sit flat in material/. Prefer whichever exists.
            var materialDir = $"chara/human/c{race}/obj/{partDir}/{partLetter}{partId}/material";
            var versioned = $"{materialDir}/v0001{materialName}";
            if (_assets.FileExists(versioned))
                return versioned;
            var flat = $"{materialDir}{materialName}";
            if (_assets.FileExists(flat))
                return flat;

            // Modded models often name a custom skin material (e.g. Bibo+
            // "/mt_c0201b0001_bibo.mtrl") that a *separate* body mod provides
            // in game — Penumbra's "auto skin assign" territory. When no
            // linked source supplies it, fall back to the vanilla "_a" skin
            // material (same body, then the gender-base body) so the skin
            // still renders instead of going blank.
            if (partLetter == "b")
            {
                foreach (var fallbackRace in (string[])[race, GenderBaseRace(race)])
                {
                    var fallback = $"chara/human/c{fallbackRace}/obj/body/b{partId}/material/v0001/mt_c{fallbackRace}b{partId}_a.mtrl";
                    if (fallback != versioned && _assets.FileExists(fallback))
                    {
                        _logger.LogInformation(
                            "Body material {Name} not found; falling back to vanilla skin {Fallback}",
                            materialName, fallback);
                        return fallback;
                    }
                }
            }

            return versioned;
        }

        return $"{model.MaterialBasePath}/v{model.MaterialSet:D4}{materialName}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/mt_c(\d{4})([bfhtz])(\d{4})_")]
    private static partial System.Text.RegularExpressions.Regex BodyPartMaterialRegex();

    internal static string SlotSuffix(EquipSlot slot) => slot switch
    {
        EquipSlot.Head => "met",
        EquipSlot.Body => "top",
        EquipSlot.Hands => "glv",
        EquipSlot.Legs => "dwn",
        EquipSlot.Feet => "sho",
        EquipSlot.Ears => "ear",
        EquipSlot.Neck => "nek",
        EquipSlot.Wrists => "wrs",
        EquipSlot.RightRing => "rir",
        EquipSlot.LeftRing => "ril",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>The equip slot a model file-name suffix stands for, or null for an unknown suffix.</summary>
    internal static EquipSlot? SlotFromSuffix(string suffix) => suffix switch
    {
        "met" => EquipSlot.Head,
        "top" => EquipSlot.Body,
        "glv" => EquipSlot.Hands,
        "dwn" => EquipSlot.Legs,
        "sho" => EquipSlot.Feet,
        "ear" => EquipSlot.Ears,
        "nek" => EquipSlot.Neck,
        "wrs" => EquipSlot.Wrists,
        "rir" => EquipSlot.RightRing,
        "ril" => EquipSlot.LeftRing,
        _ => null,
    };

    private static int SlotImcPart(EquipSlot slot) => slot switch
    {
        EquipSlot.Head or EquipSlot.Ears => 0,
        EquipSlot.Body or EquipSlot.Neck => 1,
        EquipSlot.Hands or EquipSlot.Wrists => 2,
        EquipSlot.Legs or EquipSlot.RightRing => 3,
        EquipSlot.Feet or EquipSlot.LeftRing => 4,
        _ => 0,
    };
}

public sealed record ResolvedModelInfo(string MdlPath, string MaterialBasePath, int MaterialSet);

/// <summary>One selectable model version: race/gender code (e.g. "0801") and display label ("Miqo'te ♀").</summary>
public sealed record RaceVariant(string Code, string Label);

public sealed class AssetNotFoundException(string message) : Exception(message);
