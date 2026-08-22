using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Moonlace.GameData.Meta;
using Moonlace.GameData.Upgrade;

namespace Moonlace.GameData.Tests;

/// <summary>
/// TexTools .meta/.rgsp blobs become Penumbra manipulations: parser tests
/// against hand-built binaries (the xivModdingFramework serialization
/// format) and a full ttmp extraction round trip. No game data needed.
/// </summary>
public sealed class MetaManipulationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moonlace-meta-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // --- binary builders (mirror xivModdingFramework's ItemMetadata.Serialize) ---

    private static byte[] BuildMetaFile(string targetPath, params (uint Type, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2u); // metadata version
        writer.Write(Encoding.UTF8.GetBytes(targetPath));
        writer.Write((byte)0);
        writer.Write((uint)chunks.Length);
        writer.Write(12u); // per-chunk header size
        writer.Write((uint)(stream.Position + 4));

        var headerStart = (int)stream.Position;
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Type);
            writer.Write(0u); // offset, patched below
            writer.Write(0u); // size, patched below
        }

        var offsets = new uint[chunks.Length];
        for (var i = 0; i < chunks.Length; i++)
        {
            offsets[i] = (uint)stream.Position;
            writer.Write(chunks[i].Data);
        }

        var bytes = stream.ToArray();
        for (var i = 0; i < chunks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerStart + i * 12 + 4), offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerStart + i * 12 + 8), (uint)chunks[i].Data.Length);
        }

        return bytes;
    }

    private static byte[] ImcChunk(params (byte Material, byte Decal, ushort Mask, byte Vfx, byte Anim)[] variants)
    {
        var data = new byte[variants.Length * 6];
        for (var i = 0; i < variants.Length; i++)
        {
            var span = data.AsSpan(i * 6);
            span[0] = variants[i].Material;
            span[1] = variants[i].Decal;
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], variants[i].Mask);
            span[4] = variants[i].Vfx;
            span[5] = variants[i].Anim;
        }

        return data;
    }

    private static byte[] EqdpChunk(params (uint RaceCode, byte Bits)[] entries)
    {
        var data = new byte[entries.Length * 5];
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 5), entries[i].RaceCode);
            data[i * 5 + 4] = entries[i].Bits;
        }

        return data;
    }

    private static byte[] EstChunk(params (ushort RaceCode, ushort SetId, ushort SkelId)[] entries)
    {
        var data = new byte[entries.Length * 6];
        for (var i = 0; i < entries.Length; i++)
        {
            var span = data.AsSpan(i * 6);
            BinaryPrimitives.WriteUInt16LittleEndian(span, entries[i].RaceCode);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], entries[i].SetId);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], entries[i].SkelId);
        }

        return data;
    }

    private static byte[] GmpChunk(bool enabled, bool animated, ushort rotA, ushort rotB, ushort rotC, byte unknownA, byte unknownB)
    {
        var value = (enabled ? 1u : 0u) | (animated ? 2u : 0u)
            | ((uint)(rotA & 0x3FF) << 2) | ((uint)(rotB & 0x3FF) << 12) | ((uint)(rotC & 0x3FF) << 22);
        var data = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        data[4] = (byte)((unknownB << 4) | (unknownA & 0x0F));
        return data;
    }

    private static byte[] BuildRgsp(byte version, byte clan, byte gender, params float[] floats)
    {
        var bytes = new List<byte>();
        if (version >= 2)
        {
            bytes.Add(255);
            bytes.AddRange(BitConverter.GetBytes((ushort)version));
        }

        bytes.Add(clan);
        bytes.Add(gender);
        foreach (var value in floats)
            bytes.AddRange(BitConverter.GetBytes(value));
        return bytes.ToArray();
    }

    private static JsonElement Parse(string gamePath, byte[] data, List<string>? warnings = null)
    {
        var manipulations = TexToolsMetaParser.Parse(gamePath, data, warnings ?? []);
        var array = manipulations.Select(m => m.ToJson()).ToList();
        return JsonDocument.Parse("[" + string.Join(",", array.Select(a => a.ToJsonString())) + "]").RootElement;
    }

    [Fact]
    public void EquipmentMetaTranslatesEveryChunkType()
    {
        var data = BuildMetaFile("chara/equipment/e0653/e0653_top.meta",
            ((uint)1, ImcChunk((1, 0, 0b0000_0000_0001_1111, 0, 0), (2, 3, 0b0001_0100_0000_0001, 5, 6))),
            ((uint)2, EqdpChunk((101, 0b11), (201, 0b10), (9999, 0b01))),
            ((uint)3, new byte[] { 0x3F, 0x01 }),
            ((uint)4, EstChunk((201, 653, 42))),
            ((uint)5, GmpChunk(enabled: true, animated: false, rotA: 90, rotB: 180, rotC: 270, unknownA: 3, unknownB: 7)));

        var warnings = new List<string>();
        var result = Parse("chara/equipment/e0653/e0653_top.meta", data, warnings);
        Assert.Empty(warnings);

        var imc = result.EnumerateArray().Where(m => m.GetProperty("Type").GetString() == "Imc").ToList();
        Assert.Equal(2, imc.Count);
        var imc1 = imc[1].GetProperty("Manipulation");
        Assert.Equal(653, imc1.GetProperty("PrimaryId").GetInt32());
        Assert.Equal(1, imc1.GetProperty("Variant").GetInt32());
        Assert.Equal("Equipment", imc1.GetProperty("ObjectType").GetString());
        Assert.Equal("Body", imc1.GetProperty("EquipSlot").GetString());
        Assert.Equal("Unknown", imc1.GetProperty("BodySlot").GetString());
        Assert.Equal(2, imc1.GetProperty("Entry").GetProperty("MaterialId").GetInt32());
        Assert.Equal(1, imc1.GetProperty("Entry").GetProperty("AttributeMask").GetInt32()); // low 10 bits
        Assert.Equal(5, imc1.GetProperty("Entry").GetProperty("SoundId").GetInt32());       // high 6 bits
        Assert.Equal(5, imc1.GetProperty("Entry").GetProperty("VfxId").GetInt32());

        // Body EQP bytes sit at offset 0, so the value is the raw ushort.
        var eqp = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Eqp").GetProperty("Manipulation");
        Assert.Equal(0x013Ful, eqp.GetProperty("Entry").GetUInt64());
        Assert.Equal(653, eqp.GetProperty("SetId").GetInt32());
        Assert.Equal("Body", eqp.GetProperty("Slot").GetString());

        // Body EQDP bits shift to bit offset 2; the unknown race code 9999 is dropped.
        var eqdp = result.EnumerateArray().Where(m => m.GetProperty("Type").GetString() == "Eqdp")
            .Select(m => m.GetProperty("Manipulation")).ToList();
        Assert.Equal(2, eqdp.Count);
        Assert.Equal(0b1100, eqdp[0].GetProperty("Entry").GetInt32());
        Assert.Equal("Male", eqdp[0].GetProperty("Gender").GetString());
        Assert.Equal("Midlander", eqdp[0].GetProperty("Race").GetString());
        Assert.Equal(0b1000, eqdp[1].GetProperty("Entry").GetInt32());
        Assert.Equal("Female", eqdp[1].GetProperty("Gender").GetString());

        // A body-slot EST is the "Body" skeleton table and keeps its own set id.
        var est = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Est").GetProperty("Manipulation");
        Assert.Equal("Body", est.GetProperty("Slot").GetString());
        Assert.Equal(42, est.GetProperty("Entry").GetInt32());
        Assert.Equal(653, est.GetProperty("SetId").GetInt32());
        Assert.Equal("Female", est.GetProperty("Gender").GetString());

        var gmp = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Gmp").GetProperty("Manipulation");
        Assert.Equal(653, gmp.GetProperty("SetId").GetInt32());
        var gmpEntry = gmp.GetProperty("Entry");
        Assert.True(gmpEntry.GetProperty("Enabled").GetBoolean());
        Assert.False(gmpEntry.GetProperty("Animated").GetBoolean());
        Assert.Equal(90, gmpEntry.GetProperty("RotationA").GetInt32());
        Assert.Equal(180, gmpEntry.GetProperty("RotationB").GetInt32());
        Assert.Equal(270, gmpEntry.GetProperty("RotationC").GetInt32());
        Assert.Equal(3, gmpEntry.GetProperty("UnknownA").GetInt32());
        Assert.Equal(7, gmpEntry.GetProperty("UnknownB").GetInt32());
    }

    [Fact]
    public void HeadSlotShiftsEqpIntoTheHighBytes()
    {
        var data = BuildMetaFile("chara/equipment/e0100/e0100_met.meta",
            ((uint)3, new byte[] { 0x01, 0x02, 0x03 }),
            ((uint)2, EqdpChunk((1101, 0b11))));

        var result = Parse("chara/equipment/e0100/e0100_met.meta", data);

        var eqp = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Eqp").GetProperty("Manipulation");
        Assert.Equal(0x030201ul << 40, eqp.GetProperty("Entry").GetUInt64());
        Assert.Equal("Head", eqp.GetProperty("Slot").GetString());

        // Head EQDP bits sit at offset 0.
        var eqdp = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Eqdp").GetProperty("Manipulation");
        Assert.Equal(0b11, eqdp.GetProperty("Entry").GetInt32());
        Assert.Equal("Lalafell", eqdp.GetProperty("Race").GetString());
        Assert.Equal("Head", eqdp.GetProperty("Slot").GetString());
    }

    [Fact]
    public void AccessoryMetaUsesAccessorySlots()
    {
        var data = BuildMetaFile("chara/accessory/a0053/a0053_ear.meta",
            ((uint)1, ImcChunk((1, 0, 0, 0, 0))),
            ((uint)2, EqdpChunk((1801, 0b10))));

        var result = Parse("chara/accessory/a0053/a0053_ear.meta", data);

        var imc = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Imc").GetProperty("Manipulation");
        Assert.Equal("Accessory", imc.GetProperty("ObjectType").GetString());
        Assert.Equal("Ears", imc.GetProperty("EquipSlot").GetString());

        // Ears is the first accessory EQDP slot, offset 0.
        var eqdp = result.EnumerateArray().Single(m => m.GetProperty("Type").GetString() == "Eqdp").GetProperty("Manipulation");
        Assert.Equal(0b10, eqdp.GetProperty("Entry").GetInt32());
        Assert.Equal("Viera", eqdp.GetProperty("Race").GetString());
        Assert.Equal("Female", eqdp.GetProperty("Gender").GetString());
        Assert.Equal("Ears", eqdp.GetProperty("Slot").GetString());
    }

    [Fact]
    public void WeaponMetaImcTargetsTheBodySlot()
    {
        var data = BuildMetaFile("chara/weapon/w0201/obj/body/b0001/w0201b0001.meta",
            ((uint)1, ImcChunk((1, 0, 0, 0, 0), (2, 0, 0, 0, 0))));

        var result = Parse("chara/weapon/w0201/obj/body/b0001/w0201b0001.meta", data);

        var imc = result.EnumerateArray().Select(m => m.GetProperty("Manipulation")).ToList();
        Assert.Equal(2, imc.Count);
        Assert.Equal("Weapon", imc[0].GetProperty("ObjectType").GetString());
        Assert.Equal(201, imc[0].GetProperty("PrimaryId").GetInt32());
        Assert.Equal(1, imc[0].GetProperty("SecondaryId").GetInt32());
        Assert.Equal("Body", imc[0].GetProperty("BodySlot").GetString());
        Assert.Equal("Unknown", imc[0].GetProperty("EquipSlot").GetString());
        Assert.Equal(1, imc[1].GetProperty("Variant").GetInt32());
    }

    [Fact]
    public void HairMetaEstUsesTheHairTable()
    {
        var data = BuildMetaFile("chara/human/c0801/obj/hair/h0005/c0801h0005.meta",
            ((uint)4, EstChunk((801, 5, 351))));

        var result = Parse("chara/human/c0801/obj/hair/h0005/c0801h0005.meta", data);

        var est = result.EnumerateArray().Single().GetProperty("Manipulation");
        Assert.Equal("Est", result.EnumerateArray().Single().GetProperty("Type").GetString());
        Assert.Equal("Hair", est.GetProperty("Slot").GetString());
        Assert.Equal(5, est.GetProperty("SetId").GetInt32());
        Assert.Equal(351, est.GetProperty("Entry").GetInt32());
        Assert.Equal("Miqote", est.GetProperty("Race").GetString());
        Assert.Equal("Female", est.GetProperty("Gender").GetString());
    }

    [Fact]
    public void RgspVersion2FemaleYieldsAllTenAttributes()
    {
        var data = BuildRgsp(2, clan: 14, gender: 1,
            0.5f, 1.5f, 0.9f, 1.1f, 0.8f, 0.85f, 0.9f, 1.2f, 1.25f, 1.3f);
        Assert.Equal(45, data.Length);

        var result = Parse("chara/xls/charamake/rgsp/14-1.rgsp", data);

        var manipulations = result.EnumerateArray().ToList();
        Assert.Equal(10, manipulations.Count);
        Assert.All(manipulations, m => Assert.Equal("Rsp", m.GetProperty("Type").GetString()));
        Assert.All(manipulations, m => Assert.Equal("Rava", m.GetProperty("Manipulation").GetProperty("SubRace").GetString()));
        var byAttribute = manipulations.ToDictionary(
            m => m.GetProperty("Manipulation").GetProperty("Attribute").GetString()!,
            m => m.GetProperty("Manipulation").GetProperty("Entry").GetSingle());
        Assert.Equal(0.5f, byAttribute["FemaleMinSize"]);
        Assert.Equal(1.1f, byAttribute["FemaleMaxTail"]);
        Assert.Equal(0.8f, byAttribute["BustMinX"]);
        Assert.Equal(1.3f, byAttribute["BustMaxZ"]);
    }

    [Fact]
    public void RgspVersion1MaleYieldsTheFourMaleAttributes()
    {
        var data = BuildRgsp(1, clan: 0, gender: 0,
            0.7f, 1.2f, 1f, 1f, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.Equal(42, data.Length);

        var result = Parse("chara/xls/charamake/rgsp/0-0.rgsp", data);

        var byAttribute = result.EnumerateArray().ToDictionary(
            m => m.GetProperty("Manipulation").GetProperty("Attribute").GetString()!,
            m => m.GetProperty("Manipulation").GetProperty("Entry").GetSingle());
        Assert.Equal(4, byAttribute.Count);
        Assert.Equal(0.7f, byAttribute["MaleMinSize"]);
        Assert.Equal(1.2f, byAttribute["MaleMaxSize"]);
        Assert.All(result.EnumerateArray(),
            m => Assert.Equal("Midlander", m.GetProperty("Manipulation").GetProperty("SubRace").GetString()));
    }

    [Fact]
    public void MalformedBlobsThrowInsteadOfProducingGarbage()
    {
        Assert.Throws<MetaParseException>(() =>
            TexToolsMetaParser.Parse("chara/equipment/e0001/e0001_top.meta", [1, 2, 3], []));
        Assert.Throws<MetaParseException>(() =>
            TexToolsMetaParser.Parse("chara/xls/charamake/rgsp/0-0.rgsp", new byte[10], []));
        Assert.Throws<MetaParseException>(() =>
            TexToolsMetaParser.Parse("not/a/meta/path.meta", BuildMetaFile("not/a/meta/path.meta"), []));
    }

    // --- full ttmp extraction ---

    /// <summary>A single-block uncompressed SqPack type-2 entry, the format TTMPD.mpd stores files in.</summary>
    private static byte[] SqPackType2(byte[] data)
    {
        const int headerSize = 128;
        var result = new byte[headerSize + 16 + data.Length];
        var span = result.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)(16 + data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[headerSize..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(headerSize + 8)..], 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(headerSize + 12)..], (uint)data.Length);
        data.CopyTo(span[(headerSize + 16)..]);
        return result;
    }

    [Fact]
    public void TtmpExtractionTranslatesMetadataIntoManipulations()
    {
        const string metaPath = "chara/equipment/e0653/e0653_top.meta";
        var metaBlob = SqPackType2(BuildMetaFile(metaPath,
            ((uint)1, ImcChunk((1, 0, 31, 0, 0))),
            ((uint)3, new byte[] { 0x3F, 0x01 })));
        var rgspBlob = SqPackType2(BuildRgsp(2, clan: 14, gender: 1,
            0.5f, 1.5f, 0.9f, 1.1f, 0.8f, 0.85f, 0.9f, 1.2f, 1.25f, 1.3f));
        var texBlob = SqPackType2([1, 2, 3, 4]);

        var mpd = new MemoryStream();
        long metaOffset = mpd.Position;
        mpd.Write(metaBlob);
        long rgspOffset = mpd.Position;
        mpd.Write(rgspBlob);
        long texOffset = mpd.Position;
        mpd.Write(texBlob);

        var manifest = new
        {
            TTMPVersion = "2.0",
            Name = "Meta Mod",
            SimpleModsList = new object[]
            {
                new { Name = "Meta", FullPath = metaPath, ModOffset = metaOffset, ModSize = metaBlob.Length },
                // The same meta referenced twice dedupes to one set of manipulations.
                new { Name = "Meta again", FullPath = metaPath, ModOffset = metaOffset, ModSize = metaBlob.Length },
                new { Name = "Scaling", FullPath = "chara/xls/charamake/rgsp/14-1.rgsp", ModOffset = rgspOffset, ModSize = rgspBlob.Length },
                new { Name = "Tex", FullPath = "chara/common/texture/test.tex", ModOffset = texOffset, ModSize = texBlob.Length },
            },
        };

        Directory.CreateDirectory(_root);
        var ttmpPath = Path.Combine(_root, "meta.ttmp2");
        using (var stream = File.Create(ttmpPath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("TTMPL.mpl").Open()))
                writer.Write(JsonSerializer.Serialize(manifest));
            using (var data = zip.CreateEntry("TTMPD.mpd").Open())
                data.Write(mpd.ToArray());
        }

        var extractDir = Path.Combine(_root, "extracted");
        var warnings = new List<string>();
        ModpackFile.ExtractToFolder(ttmpPath, extractDir, warnings);
        Assert.Empty(warnings);

        var defaultMod = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractDir, "default_mod.json"))).RootElement;

        // The meta path is not a file redirection, and no meta file was written.
        Assert.False(defaultMod.GetProperty("Files").TryGetProperty(metaPath, out _));
        Assert.True(defaultMod.GetProperty("Files").TryGetProperty("chara/common/texture/test.tex", out _));
        Assert.Empty(Directory.EnumerateFiles(extractDir, "*.meta", SearchOption.AllDirectories));

        // 1 IMC + 1 EQP (deduped from the double reference) + 10 RSP entries.
        var manipulations = defaultMod.GetProperty("Manipulations").EnumerateArray().ToList();
        Assert.Equal(12, manipulations.Count);
        Assert.Equal(1, manipulations.Count(m => m.GetProperty("Type").GetString() == "Imc"));
        Assert.Equal(1, manipulations.Count(m => m.GetProperty("Type").GetString() == "Eqp"));
        Assert.Equal(10, manipulations.Count(m => m.GetProperty("Type").GetString() == "Rsp"));
    }
}
