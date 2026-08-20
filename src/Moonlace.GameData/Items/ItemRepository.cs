using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Items;

/// <summary>
/// Loads the browsable models: gear and accessories from the Item Excel
/// sheet, plus character body parts (faces, hair, tails, bodies) enumerated
/// by probing the chara/human model paths per race.
/// </summary>
public sealed class ItemRepository : IItemRepository
{
    /// <summary>Synthetic RowId base for body parts — far above real Item sheet rows, stable across runs.</summary>
    private const uint BodyPartRowBase = 0x4000_0000;

    private const int MaxFaceNumber = 300;
    private const int MaxTailNumber = 100;
    private const int MaxBodyNumber = 300;
    private const int MaxHairNumber = 300;

    private readonly LuminaGameDataService _gameData;
    private readonly ILogger<ItemRepository> _logger;

    public ItemRepository(LuminaGameDataService gameData, ILogger<ItemRepository> logger)
    {
        _gameData = gameData;
        _logger = logger;
    }

    public Task<IReadOnlyList<EquipmentItem>> GetEquipmentItemsAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<EquipmentItem>>(() =>
        {
            var sheet = _gameData.Lumina.GetExcelSheet<Item>()
                ?? throw new InvalidOperationException("Item sheet not found in game data.");

            var result = new List<EquipmentItem>();
            foreach (var row in sheet)
            {
                ct.ThrowIfCancellationRequested();

                if (row.ModelMain == 0 || row.EquipSlotCategory.RowId == 0)
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var slot = MapSlot(row.EquipSlotCategory.Value);
                if (slot is null)
                    continue;

                var quad = row.ModelMain;
                result.Add(new EquipmentItem
                {
                    RowId = row.RowId,
                    Name = name,
                    Slot = slot.Value,
                    ModelId = (ushort)(quad & 0xFFFF),
                    SecondaryId = (ushort)((quad >> 16) & 0xFFFF),
                    // Weapons carry the variant in the third u16, equipment in the second.
                    Variant = slot is EquipSlot.MainHand or EquipSlot.OffHand
                        ? (ushort)((quad >> 32) & 0xFFFF)
                        : (ushort)((quad >> 16) & 0xFFFF),
                });
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var itemCount = result.Count;

            // Body parts follow, in race → part-number order (the browser
            // groups by kind and keeps this order inside each group).
            AppendBodyParts(result, ct);

            _logger.LogInformation("Loaded {Items} gear/accessory items and {Parts} body parts",
                itemCount, result.Count - itemCount);
            return result;
        }, ct);
    }

    /// <summary>
    /// Body parts have no Excel sheet; they are found by probing the model
    /// paths for every race and part number. Existence checks are index
    /// lookups, so the full sweep is cheap.
    /// </summary>
    private void AppendBodyParts(List<EquipmentItem> result, CancellationToken ct)
    {
        var kinds = new (EquipSlot Slot, string Dir, char Letter, string ModelSuffix, string Label, int Max)[]
        {
            (EquipSlot.Face, "face", 'f', "fac", "Face", MaxFaceNumber),
            (EquipSlot.Tail, "tail", 't', "til", "Tail", MaxTailNumber),
            (EquipSlot.HumanBody, "body", 'b', "top", "Body", MaxBodyNumber),
            (EquipSlot.Hair, "hair", 'h', "hir", "Hair", MaxHairNumber),
        };

        foreach (var (race, label) in AssetPathResolver.RaceTable)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var kind in kinds)
            {
                for (var n = 1; n <= kind.Max; n++)
                {
                    var part = $"{kind.Letter}{n:D4}";
                    var mdl = $"chara/human/c{race}/obj/{kind.Dir}/{part}/model/c{race}{part}_{kind.ModelSuffix}.mdl";
                    if (!_gameData.Lumina.FileExists(mdl))
                        continue;

                    result.Add(new EquipmentItem
                    {
                        RowId = BodyPartRowId(kind.Slot, race, n),
                        Name = $"{label} {kind.Label} {n}",
                        Slot = kind.Slot,
                        ModelId = (ushort)n,
                        SecondaryId = 0,
                        Variant = 1,
                        RaceCode = race,
                    });
                }
            }
        }
    }

    /// <summary>Stable synthetic id: kind, race code and part number packed above the Item sheet range.</summary>
    private static uint BodyPartRowId(EquipSlot slot, string race, int number)
    {
        var kind = slot switch
        {
            EquipSlot.Face => 0u,
            EquipSlot.Tail => 1u,
            EquipSlot.HumanBody => 2u,
            EquipSlot.Hair => 3u,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        return BodyPartRowBase | (kind << 24) | (uint.Parse(race) << 12) | (uint)number;
    }

    private static EquipSlot? MapSlot(EquipSlotCategory category)
    {
        if (category.MainHand != 0)
            return EquipSlot.MainHand;
        if (category.OffHand != 0)
            return EquipSlot.OffHand;
        if (category.Head != 0)
            return EquipSlot.Head;
        if (category.Body != 0)
            return EquipSlot.Body;
        if (category.Gloves != 0)
            return EquipSlot.Hands;
        if (category.Legs != 0)
            return EquipSlot.Legs;
        if (category.Feet != 0)
            return EquipSlot.Feet;
        if (category.Ears != 0)
            return EquipSlot.Ears;
        if (category.Neck != 0)
            return EquipSlot.Neck;
        if (category.Wrists != 0)
            return EquipSlot.Wrists;
        if (category.FingerR != 0)
            return EquipSlot.RightRing;
        if (category.FingerL != 0)
            return EquipSlot.LeftRing;
        return null;
    }
}
