using System.Text.Json.Nodes;

namespace Moonlace.GameData.Meta;

/// <summary>
/// One Penumbra metadata manipulation, ready for the "Manipulations" array of
/// a mod's default_mod.json or group option JSON. <see cref="IdentityKey"/>
/// names the manipulated target without its value, mirroring Penumbra's
/// dedup rule (one entry per target per option, first one wins).
/// </summary>
public sealed record MetaManipulation(string Type, JsonObject Manipulation, string IdentityKey)
{
    /// <summary>A fresh {"Type", "Manipulation"} node, safe to attach to any parent.</summary>
    public JsonObject ToJson() => new()
    {
        ["Type"] = Type,
        ["Manipulation"] = (JsonObject)Manipulation.DeepClone(),
    };
}

/// <summary>An IMC variant entry; AttributeAndSound packs a 10-bit attribute mask and a 6-bit sound id.</summary>
public readonly record struct ImcEntry(
    byte MaterialId,
    byte DecalId,
    ushort AttributeAndSound,
    byte VfxId,
    byte MaterialAnimationId)
{
    public ushort AttributeMask => (ushort)(AttributeAndSound & 0x3FF);

    public byte SoundId => (byte)(AttributeAndSound >> 10);
}

/// <summary>A gimmick (visor) entry, unpacked from its 5-byte game representation.</summary>
public readonly record struct GmpEntry(
    bool Enabled,
    bool Animated,
    ushort RotationA,
    ushort RotationB,
    ushort RotationC,
    byte UnknownA,
    byte UnknownB)
{
    public static GmpEntry FromBytes(ReadOnlySpan<byte> data)
    {
        var value = BitConverter.ToUInt32(data[..4]);
        return new GmpEntry(
            Enabled: (value & 1) != 0,
            Animated: (value & 2) != 0,
            RotationA: (ushort)((value >> 2) & 0x3FF),
            RotationB: (ushort)((value >> 12) & 0x3FF),
            RotationC: (ushort)((value >> 22) & 0x3FF),
            UnknownA: (byte)(data[4] & 0x0F),
            UnknownB: (byte)((data[4] >> 4) & 0x0F));
    }
}

/// <summary>
/// Builds Penumbra-format metadata manipulations, using the exact JSON
/// shapes and enum names Penumbra's mod reader expects (cross-checked
/// against Penumbra's schemas/structs/meta_*.json and TexTools' own
/// TTMP-to-PMP converter).
/// </summary>
public static class PenumbraMeta
{
    public static MetaManipulation Imc(
        string objectType, ushort primaryId, ushort secondaryId, byte variant,
        string equipSlot, string bodySlot, ImcEntry entry) => new(
        "Imc",
        new JsonObject
        {
            ["Entry"] = new JsonObject
            {
                ["MaterialId"] = entry.MaterialId,
                ["DecalId"] = entry.DecalId,
                ["VfxId"] = entry.VfxId,
                ["MaterialAnimationId"] = entry.MaterialAnimationId,
                ["AttributeMask"] = entry.AttributeMask,
                ["SoundId"] = entry.SoundId,
            },
            ["PrimaryId"] = primaryId,
            ["SecondaryId"] = secondaryId,
            ["Variant"] = variant,
            ["ObjectType"] = objectType,
            ["EquipSlot"] = equipSlot,
            ["BodySlot"] = bodySlot,
        },
        $"Imc:{objectType}:{primaryId}:{secondaryId}:{variant}:{equipSlot}:{bodySlot}");

    /// <summary>EQP flags as a raw ulong, already shifted to the slot's byte offset within the 8-byte set entry.</summary>
    public static MetaManipulation Eqp(ushort setId, string slot, ulong entry) => new(
        "Eqp",
        new JsonObject
        {
            ["Entry"] = entry,
            ["SetId"] = setId,
            ["Slot"] = slot,
        },
        $"Eqp:{setId}:{slot}");

    /// <summary>EQDP bits (1 material, 2 model) already shifted to the slot's 2-bit offset.</summary>
    public static MetaManipulation Eqdp(ushort setId, string slot, string gender, string race, ushort entry) => new(
        "Eqdp",
        new JsonObject
        {
            ["Entry"] = entry,
            ["Gender"] = gender,
            ["Race"] = race,
            ["SetId"] = setId,
            ["Slot"] = slot,
        },
        $"Eqdp:{setId}:{slot}:{gender}:{race}");

    /// <summary>Extra skeleton entry; slot is one of Hair, Face, Body, Head.</summary>
    public static MetaManipulation Est(ushort setId, string slot, string gender, string race, ushort skeletonId) => new(
        "Est",
        new JsonObject
        {
            ["Entry"] = skeletonId,
            ["Gender"] = gender,
            ["Race"] = race,
            ["SetId"] = setId,
            ["Slot"] = slot,
        },
        $"Est:{setId}:{slot}:{gender}:{race}");

    public static MetaManipulation Gmp(ushort setId, GmpEntry entry) => new(
        "Gmp",
        new JsonObject
        {
            ["Entry"] = new JsonObject
            {
                ["Enabled"] = entry.Enabled,
                ["Animated"] = entry.Animated,
                ["RotationA"] = entry.RotationA,
                ["RotationB"] = entry.RotationB,
                ["RotationC"] = entry.RotationC,
                ["UnknownA"] = entry.UnknownA,
                ["UnknownB"] = entry.UnknownB,
            },
            ["SetId"] = setId,
        },
        $"Gmp:{setId}");

    public static MetaManipulation Rsp(string subRace, string attribute, float value) => new(
        "Rsp",
        new JsonObject
        {
            ["Entry"] = value,
            ["SubRace"] = subRace,
            ["Attribute"] = attribute,
        },
        $"Rsp:{subRace}:{attribute}");

    /// <summary>Maps a model path slot suffix (top, met, ear, ...) to Penumbra's EquipSlot name.</summary>
    public static bool TryGetEquipSlot(string suffix, out string slot, out bool isAccessory)
    {
        (slot, isAccessory) = suffix switch
        {
            "met" => ("Head", false),
            "top" => ("Body", false),
            "glv" => ("Hands", false),
            "dwn" => ("Legs", false),
            "sho" => ("Feet", false),
            "ear" => ("Ears", true),
            "nek" => ("Neck", true),
            "wrs" => ("Wrists", true),
            "rir" => ("RFinger", true),
            "ril" => ("LFinger", true),
            _ => ("", false),
        };
        return slot.Length > 0;
    }

    /// <summary>Byte count and byte offset of an equipment slot inside the 8-byte EQP set entry.</summary>
    public static (int Size, int Offset)? EqpLayout(string slot) => slot switch
    {
        "Body" => (2, 0),
        "Legs" => (1, 2),
        "Hands" => (1, 3),
        "Feet" => (1, 4),
        "Head" => (3, 5),
        _ => null,
    };

    /// <summary>Bit offset of a slot's 2-bit field inside an EQDP entry.</summary>
    public static int? EqdpOffset(string slot) => slot switch
    {
        "Head" or "Ears" => 0,
        "Body" or "Neck" => 2,
        "Hands" or "Wrists" => 4,
        "Legs" or "RFinger" => 6,
        "Feet" or "LFinger" => 8,
        _ => null,
    };

    /// <summary>
    /// Splits a game race/gender code (0101 Midlander male, 0201 Midlander
    /// female, ... 1804 Viera female NPC) into Penumbra's Gender and
    /// ModelRace names. Codes outside the known set return false.
    /// </summary>
    public static bool TrySplitRaceCode(uint code, out string gender, out string race)
    {
        gender = "";
        race = "";
        var block = code / 100; // 1..18, odd male / even female
        var kind = code % 100;  // 01 player, 04 npc

        race = block switch
        {
            1 or 2 => "Midlander",
            3 or 4 => "Highlander",
            5 or 6 => "Elezen",
            7 or 8 => "Miqote",
            9 or 10 => "Roegadyn",
            11 or 12 => "Lalafell",
            13 or 14 => "AuRa",
            15 or 16 => "Hrothgar",
            17 or 18 => "Viera",
            _ => "",
        };
        if (race.Length == 0)
            return false;

        gender = (block % 2 == 1, kind) switch
        {
            (true, 1) => "Male",
            (false, 1) => "Female",
            (true, 4) => "MaleNpc",
            (false, 4) => "FemaleNpc",
            _ => "",
        };
        return gender.Length > 0;
    }

    private static readonly string[] SubRaces =
    [
        "Midlander", "Highlander", "Wildwood", "Duskwight", "Plainsfolk", "Dunesfolk",
        "SeekerOfTheSun", "KeeperOfTheMoon", "Seawolf", "Hellsguard", "Raen", "Xaela",
        "Helion", "Lost", "Rava", "Veena",
    ];

    /// <summary>Penumbra's SubRace name for a 0-based .rgsp clan index, or null when out of range.</summary>
    public static string? SubRaceName(int index) =>
        index >= 0 && index < SubRaces.Length ? SubRaces[index] : null;
}
