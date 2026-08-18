namespace Moonlace.Core.Models;

/// <summary>
/// What kind of browsable model an item is. Gear and accessory values map to
/// real equip slots; Face/Tail/HumanBody are character body parts that are
/// browsable models but not equippable items.
/// </summary>
public enum EquipSlot
{
    // Gear
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,

    // Accessories
    Ears,
    Neck,
    Wrists,
    RightRing,
    LeftRing,

    // Character body parts (chara/human)
    Face,
    Tail,
    HumanBody,
}
