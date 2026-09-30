using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Moonlace.GameData.Meta;

/// <summary>
/// Reads the game's EQDP tables (chara/xls/charadb/{equipment|accessory}deformerparameter/c{race}.eqdp):
/// per race, set and slot, whether the race has its own material and model.
/// When a race has no model of its own the game loads its base race's model
/// (Miqo'te ♀ → Midlander ♀ → Midlander ♂) and deforms it at runtime.
///
/// Layout (matches Penumbra's ExpandedEqdpFile): u16 identifier, u16 block
/// size, u16 block count, block count × u16 block offsets (0xFFFF = empty
/// block, every set in it is 0), then u16 entries; set N lives at
/// data[offsets[N / blockSize] + N % blockSize]. Each entry packs 2 bits per
/// slot (bit 0 material, bit 1 model) at <see cref="PenumbraMeta.EqdpOffset"/>.
/// </summary>
public sealed class EqdpTable
{
    private readonly LuminaGameDataService _gameData;
    private readonly ConcurrentDictionary<string, byte[]?> _files = new();

    public EqdpTable(LuminaGameDataService gameData)
    {
        _gameData = gameData;
    }

    /// <summary>The game's own 2-bit slot value (bit 0 material, bit 1 model), or null when the race or slot is unknown.</summary>
    public int? VanillaBits(string raceCode, ushort setId, string slot, bool accessory)
    {
        if (PenumbraMeta.EqdpOffset(slot) is not { } offset)
            return null;
        var kind = accessory ? "accessory" : "equipment";
        var data = _files.GetOrAdd($"{kind}:{raceCode}", _ => ReadFile(kind, raceCode));
        if (data is null)
            return null;
        return (EntryFor(data, setId) >> offset) & 3;
    }

    /// <summary>
    /// The effective 2-bit slot value: the last matching Eqdp manipulation
    /// in <paramref name="manipulations"/> wins, else the game's own value.
    /// </summary>
    public int? EffectiveBits(
        string raceCode, ushort setId, string slot, bool accessory, IEnumerable<JsonObject> manipulations)
    {
        if (PenumbraMeta.EqdpOffset(slot) is not { } offset)
            return null;
        if (FindManipulation(raceCode, setId, slot, manipulations) is { } entry)
            return (entry >> offset) & 3;
        return VanillaBits(raceCode, setId, slot, accessory);
    }

    /// <summary>The Entry of the last Eqdp manipulation targeting this race/set/slot, or null.</summary>
    public static int? FindManipulation(string raceCode, ushort setId, string slot, IEnumerable<JsonObject> manipulations)
    {
        if (!uint.TryParse(raceCode, out var code) || !PenumbraMeta.TrySplitRaceCode(code, out var gender, out var race))
            return null;

        int? found = null;
        foreach (var node in manipulations)
        {
            if (node["Type"]?.ToString() != "Eqdp" || node["Manipulation"] is not JsonObject m)
                continue;
            if (m["Gender"]?.ToString() == gender
                && m["Race"]?.ToString() == race
                && m["Slot"]?.ToString() == slot
                && TryReadInt(m["SetId"], out var s) && s == setId
                && TryReadInt(m["Entry"], out var e))
                found = e;
        }

        return found;
    }

    /// <summary>An Eqdp manipulation giving this race/set/slot the 2-bit value <paramref name="bits"/>.</summary>
    public static JsonObject Manipulation(string raceCode, ushort setId, string slot, int bits)
    {
        if (!uint.TryParse(raceCode, out var code) || !PenumbraMeta.TrySplitRaceCode(code, out var gender, out var race))
            throw new ArgumentException($"Unknown race code {raceCode}.", nameof(raceCode));
        var offset = PenumbraMeta.EqdpOffset(slot)
            ?? throw new ArgumentException($"Slot {slot} has no EQDP entry.", nameof(slot));
        // Round-trip through text so every number is a plain JSON number
        // (the builder's ushort-backed values would not read back as int).
        var node = PenumbraMeta.Eqdp(setId, slot, gender, race, (ushort)((bits & 3) << offset)).ToJson();
        return (JsonObject)JsonNode.Parse(node.ToJsonString())!;
    }

    /// <summary>Reads a JSON number of any backing type (ushort in memory, JsonElement from disk) as an int.</summary>
    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue && int.TryParse(node.ToJsonString(), out value);
    }

    private byte[]? ReadFile(string kind, string raceCode)
    {
        var path = $"chara/xls/charadb/{kind}deformerparameter/c{raceCode}.eqdp";
        return _gameData.Lumina.FileExists(path) ? _gameData.Lumina.GetFile(path)?.Data : null;
    }

    internal static ushort EntryFor(byte[] data, ushort setId)
    {
        if (data.Length < 6)
            return 0;
        int blockSize = BitConverter.ToUInt16(data, 2);
        int blockCount = BitConverter.ToUInt16(data, 4);
        if (blockSize == 0)
            return 0;
        var block = setId / blockSize;
        if (block >= blockCount)
            return 0;
        var blockOffset = BitConverter.ToUInt16(data, 6 + block * 2);
        if (blockOffset == 0xFFFF)
            return 0;
        var at = 6 + blockCount * 2 + (blockOffset + setId % blockSize) * 2;
        return at + 2 <= data.Length ? BitConverter.ToUInt16(data, at) : (ushort)0;
    }
}
