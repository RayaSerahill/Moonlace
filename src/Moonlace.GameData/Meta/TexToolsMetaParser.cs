using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Moonlace.GameData.Meta;

public sealed class MetaParseException(string message) : Exception(message);

/// <summary>
/// Parses the metadata blobs TexTools ships in .ttmp modpacks into Penumbra
/// manipulations: .meta files (a container of IMC/EQP/EQDP/EST/GMP chunks
/// for one item root) and .rgsp files (racial scaling parameters). The
/// binary formats follow xivModdingFramework's ItemMetadata and
/// RacialGenderScalingParameter serializers; the translation mirrors
/// Penumbra's own TexToolsMeta importer.
/// </summary>
public static partial class TexToolsMetaParser
{
    public static bool IsMetadataPath(string gamePath) =>
        gamePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
        || gamePath.EndsWith(".rgsp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Translates one metadata blob. Manipulations that cannot be expressed
    /// are reported into <paramref name="warnings"/>; a blob that cannot be
    /// read at all throws <see cref="MetaParseException"/>.
    /// </summary>
    public static List<MetaManipulation> Parse(string gamePath, byte[] data, List<string> warnings)
    {
        try
        {
            return gamePath.EndsWith(".rgsp", StringComparison.OrdinalIgnoreCase)
                ? ParseRgsp(data)
                : ParseMeta(gamePath, data, warnings);
        }
        catch (MetaParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new MetaParseException("the file is truncated or malformed.");
        }
    }

    // --- .meta (IMC/EQP/EQDP/EST/GMP container) ---

    private enum ChunkType : uint
    {
        Imc = 1,
        Eqdp = 2,
        Eqp = 3,
        Est = 4,
        Gmp = 5,
    }

    private sealed record MetaTarget(
        string ObjectType,   // Penumbra name: Equipment, Accessory, Weapon, Monster, DemiHuman, Character
        ushort PrimaryId,
        string? SecondaryType, // body, hair, face, ... for weapons/monsters/demihumans/humans
        ushort SecondaryId,
        string? EquipSlot,   // Penumbra name, when the path has a slot suffix
        bool IsAccessory);

    [GeneratedRegex(
        @"^chara/(?<type>equipment|accessory|weapon|monster|demihuman|human)/[a-z](?<pid>\d{4})(?:/obj/(?<stype>[a-z]+)/[a-z](?<sid>\d{4}))?/[a-z]\d{4}(?:[a-z]\d{4})?(?:_(?<slot>[a-z]{3}))?\.meta$")]
    private static partial Regex MetaPathRegex();

    private static MetaTarget? ParseTargetPath(string path)
    {
        var match = MetaPathRegex().Match(path.Trim().Replace('\\', '/').ToLowerInvariant());
        if (!match.Success)
            return null;

        string? slot = null;
        var isAccessory = false;
        if (match.Groups["slot"].Success && PenumbraMeta.TryGetEquipSlot(match.Groups["slot"].Value, out var name, out isAccessory))
            slot = name;

        var objectType = match.Groups["type"].Value switch
        {
            "equipment" => "Equipment",
            "accessory" => "Accessory",
            "weapon" => "Weapon",
            "monster" => "Monster",
            "demihuman" => "DemiHuman",
            _ => "Character",
        };

        return new MetaTarget(
            objectType,
            ushort.Parse(match.Groups["pid"].Value),
            match.Groups["stype"].Success ? match.Groups["stype"].Value : null,
            match.Groups["sid"].Success ? ushort.Parse(match.Groups["sid"].Value) : (ushort)0,
            slot,
            isAccessory);
    }

    private static List<MetaManipulation> ParseMeta(string gamePath, byte[] data, List<string> warnings)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        reader.ReadUInt32(); // metadata version; chunks absent in v1 are simply not present
        var targetPath = ReadNullTerminated(reader);
        var target = ParseTargetPath(targetPath.Length > 0 ? targetPath : gamePath)
            ?? throw new MetaParseException($"\"{targetPath}\" is not a recognized metadata target.");

        var chunkCount = reader.ReadUInt32();
        var chunkHeaderSize = reader.ReadUInt32();
        var chunkHeaderStart = reader.ReadUInt32();
        if (chunkCount > 64 || chunkHeaderSize < 12 || chunkHeaderStart > data.Length)
            throw new MetaParseException("the chunk table is implausible.");

        var manipulations = new List<MetaManipulation>();
        for (var i = 0; i < chunkCount; i++)
        {
            reader.BaseStream.Seek(chunkHeaderStart + i * chunkHeaderSize, SeekOrigin.Begin);
            var type = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var size = reader.ReadInt32();
            if (size < 0 || offset + (uint)size > data.Length)
                throw new MetaParseException("a chunk points outside the file.");

            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            var chunk = reader.ReadBytes(size);
            switch ((ChunkType)type)
            {
                case ChunkType.Imc:
                    TranslateImc(target, chunk, manipulations, warnings);
                    break;
                case ChunkType.Eqdp:
                    TranslateEqdp(target, chunk, manipulations, warnings);
                    break;
                case ChunkType.Eqp:
                    TranslateEqp(target, chunk, manipulations, warnings);
                    break;
                case ChunkType.Est:
                    TranslateEst(target, chunk, manipulations, warnings);
                    break;
                case ChunkType.Gmp:
                    TranslateGmp(target, chunk, manipulations, warnings);
                    break;
                default:
                    warnings.Add($"{targetPath}: unknown metadata chunk type {type} was skipped.");
                    break;
            }
        }

        return manipulations;
    }

    private static void TranslateImc(MetaTarget target, byte[] chunk, List<MetaManipulation> manipulations, List<string> warnings)
    {
        string equipSlot;
        string bodySlot;
        ushort secondaryId;
        switch (target.ObjectType)
        {
            case "Equipment" or "Accessory" or "DemiHuman":
                if (target.EquipSlot is null)
                {
                    warnings.Add($"IMC entries for {target.ObjectType} {target.PrimaryId} have no equip slot and were skipped.");
                    return;
                }

                equipSlot = target.EquipSlot;
                bodySlot = "Unknown";
                secondaryId = target.ObjectType == "DemiHuman" ? target.SecondaryId : (ushort)0;
                break;

            case "Weapon" or "Monster":
                equipSlot = "Unknown";
                bodySlot = "Body";
                secondaryId = target.SecondaryId;
                break;

            default:
                warnings.Add($"IMC entries in a {target.ObjectType} .meta cannot be translated and were skipped.");
                return;
        }

        var count = chunk.Length / 6;
        for (var variant = 0; variant < count; variant++)
        {
            if (variant > byte.MaxValue)
            {
                warnings.Add($"IMC variants above 255 for {target.ObjectType} {target.PrimaryId} were skipped.");
                break;
            }

            var span = chunk.AsSpan(variant * 6, 6);
            var entry = new ImcEntry(
                MaterialId: span[0],
                DecalId: span[1],
                AttributeAndSound: BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
                VfxId: span[4],
                MaterialAnimationId: span[5]);
            manipulations.Add(PenumbraMeta.Imc(
                target.ObjectType, target.PrimaryId, secondaryId, (byte)variant, equipSlot, bodySlot, entry));
        }
    }

    private static void TranslateEqdp(MetaTarget target, byte[] chunk, List<MetaManipulation> manipulations, List<string> warnings)
    {
        if (target.EquipSlot is null || PenumbraMeta.EqdpOffset(target.EquipSlot) is not { } shift)
        {
            warnings.Add($"EQDP entries for {target.ObjectType} {target.PrimaryId} have no equip slot and were skipped.");
            return;
        }

        var count = chunk.Length / 5;
        for (var i = 0; i < count; i++)
        {
            var span = chunk.AsSpan(i * 5, 5);
            var raceCode = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (!PenumbraMeta.TrySplitRaceCode(raceCode, out var gender, out var race))
                continue; // same silent skip as Penumbra for codes it does not model

            var bits = (ushort)((span[4] & 0x3) << shift);
            manipulations.Add(PenumbraMeta.Eqdp(target.PrimaryId, target.EquipSlot, gender, race, bits));
        }
    }

    private static void TranslateEqp(MetaTarget target, byte[] chunk, List<MetaManipulation> manipulations, List<string> warnings)
    {
        if (target.ObjectType != "Equipment" || target.EquipSlot is null
            || PenumbraMeta.EqpLayout(target.EquipSlot) is not var (size, offset))
        {
            warnings.Add($"EQP entries for {target.ObjectType} {target.PrimaryId} do not target an equipment slot and were skipped.");
            return;
        }

        if (chunk.Length != size)
        {
            warnings.Add($"EQP entry for set {target.PrimaryId} has {chunk.Length} bytes where {size} were expected and was skipped.");
            return;
        }

        var value = 0ul;
        for (var i = 0; i < size; i++)
            value |= (ulong)chunk[i] << ((offset + i) * 8);
        manipulations.Add(PenumbraMeta.Eqp(target.PrimaryId, target.EquipSlot, value));
    }

    private static void TranslateEst(MetaTarget target, byte[] chunk, List<MetaManipulation> manipulations, List<string> warnings)
    {
        var estSlot = (target.SecondaryType, target.EquipSlot) switch
        {
            ("face", _) => "Face",
            ("hair", _) => "Hair",
            (_, "Head") => "Head",
            (_, "Body") => "Body",
            _ => null,
        };
        if (estSlot is null)
        {
            warnings.Add($"EST entries for {target.ObjectType} {target.PrimaryId} have no skeleton slot and were skipped.");
            return;
        }

        var count = chunk.Length / 6;
        for (var i = 0; i < count; i++)
        {
            var span = chunk.AsSpan(i * 6, 6);
            var raceCode = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var setId = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            var skeletonId = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            if (!PenumbraMeta.TrySplitRaceCode(raceCode, out var gender, out var race))
                continue;

            manipulations.Add(PenumbraMeta.Est(setId, estSlot, gender, race, skeletonId));
        }
    }

    private static void TranslateGmp(MetaTarget target, byte[] chunk, List<MetaManipulation> manipulations, List<string> warnings)
    {
        if (chunk.Length < 5)
        {
            warnings.Add($"GMP entry for set {target.PrimaryId} is truncated and was skipped.");
            return;
        }

        manipulations.Add(PenumbraMeta.Gmp(target.PrimaryId, GmpEntry.FromBytes(chunk)));
    }

    private static string ReadNullTerminated(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte())
            bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // --- .rgsp (racial scaling parameters) ---

    private static List<MetaManipulation> ParseRgsp(byte[] data)
    {
        // Version 1 is [clan byte][gender byte][10 floats]; version 2+
        // prefixes [0xFF][ushort version]. Both always carry all 10 floats.
        if (data.Length != 42 && data.Length != 45)
            throw new MetaParseException($"an .rgsp file has {data.Length} bytes where 42 or 45 were expected.");

        var offset = data[0] == byte.MaxValue ? 3 : 0;
        var subRace = PenumbraMeta.SubRaceName(data[offset])
            ?? throw new MetaParseException($"{data[offset]} is not a known clan.");
        var gender = data[offset + 1];
        if (gender > 1)
            throw new MetaParseException($"{gender} is not a known gender.");
        offset += 2;

        float Next()
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            return value;
        }

        var manipulations = new List<MetaManipulation>();
        void Add(string attribute) => manipulations.Add(PenumbraMeta.Rsp(subRace, attribute, Next()));

        if (gender == 1)
        {
            Add("FemaleMinSize");
            Add("FemaleMaxSize");
            Add("FemaleMinTail");
            Add("FemaleMaxTail");
            Add("BustMinX");
            Add("BustMinY");
            Add("BustMinZ");
            Add("BustMaxX");
            Add("BustMaxY");
            Add("BustMaxZ");
        }
        else
        {
            // The male layout carries the same 10 floats; only these four are meaningful.
            Add("MaleMinSize");
            Add("MaleMaxSize");
            Add("MaleMinTail");
            Add("MaleMaxTail");
        }

        return manipulations;
    }
}
