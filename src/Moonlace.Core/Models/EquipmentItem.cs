namespace Moonlace.Core.Models;

/// <summary>
/// A browsable equipment item. Carries just enough model metadata to resolve
/// its game assets later without going back to the Excel sheets.
/// </summary>
public sealed class EquipmentItem
{
    public required uint RowId { get; init; }

    public required string Name { get; init; }

    public required EquipSlot Slot { get; init; }

    /// <summary>Primary model id (weapon id or equipment set id).</summary>
    public required ushort ModelId { get; init; }

    /// <summary>Secondary model id (weapon body id); unused for equipment.</summary>
    public required ushort SecondaryId { get; init; }

    /// <summary>IMC variant used to select the material set.</summary>
    public required ushort Variant { get; init; }

    /// <summary>Race/gender code (e.g. "0101") for body parts, whose race is part of their identity. Null otherwise.</summary>
    public string? RaceCode { get; init; }

    public bool IsWeapon => Slot is EquipSlot.MainHand or EquipSlot.OffHand;

    public bool IsAccessory => Slot is EquipSlot.Ears or EquipSlot.Neck or EquipSlot.Wrists
        or EquipSlot.RightRing or EquipSlot.LeftRing;

    public bool IsBodyPart => Slot is EquipSlot.Face or EquipSlot.Tail or EquipSlot.HumanBody
        or EquipSlot.Hair;
}
