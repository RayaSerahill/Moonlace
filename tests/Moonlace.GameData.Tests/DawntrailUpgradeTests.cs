using System.Buffers.Binary;
using System.Text.Json;
using Lumina.Data;
using Lumina.Data.Structs;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Upgrade;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Endwalker → Dawntrail upgrade: material surgery (verified through both
/// Moonlace parsers), reference-exact texture math, and the whole-mod flow
/// including backups and revert. Everything runs on fabricated files — no
/// game installation involved.
/// </summary>
public sealed class DawntrailUpgradeTests : IDisposable
{
    private const string MtrlGamePath = "chara/equipment/e9999/material/v0001/mt_c0101e9999_top_a.mtrl";
    private const string NormalGamePath = "chara/equipment/e9999/texture/v01_c0101e9999_top_n.tex";
    private const string MaskGamePath = "chara/equipment/e9999/texture/v01_c0101e9999_top_m.tex";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "moonlace-dt-upgrade-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // --- Fabrication helpers ---

    private static void WriteHalf(byte[] buffer, int halfIndex, float value) =>
        BinaryPrimitives.WriteHalfLittleEndian(buffer.AsSpan(halfIndex * 2), (Half)value);

    private static float ReadHalf(byte[] buffer, int halfIndex) =>
        (float)BinaryPrimitives.ReadHalfLittleEndian(buffer.AsSpan(halfIndex * 2));

    /// <summary>A legacy character.shpk material with known color-table values in every row.</summary>
    private static byte[] BuildLegacyMaterial(bool withDye)
    {
        var dataSet = new byte[withDye ? 544 : 512];
        for (var row = 0; row < 16; row++)
        {
            var h = row * 16;
            WriteHalf(dataSet, h + 0, 0.5f);   // diffuse
            WriteHalf(dataSet, h + 1, 0.25f);
            WriteHalf(dataSet, h + 2, 0.125f);
            WriteHalf(dataSet, h + 3, 0.75f);  // specular power (legacy slot)
            WriteHalf(dataSet, h + 4, 0.5f);   // specular
            WriteHalf(dataSet, h + 5, 0.5f);
            WriteHalf(dataSet, h + 6, 0.5f);
            WriteHalf(dataSet, h + 7, 32f);    // gloss (legacy slot)
            WriteHalf(dataSet, h + 8, 2f);     // emissive
            WriteHalf(dataSet, h + 9, 0f);
            WriteHalf(dataSet, h + 10, 0f);
            WriteHalf(dataSet, h + 11, 3f);    // subsurface material id
            WriteHalf(dataSet, h + 12, 4f);    // tile scaling
            WriteHalf(dataSet, h + 13, 4f);
            WriteHalf(dataSet, h + 14, 16f);
            WriteHalf(dataSet, h + 15, 16f);
        }

        if (withDye)
        {
            for (var row = 0; row < 16; row++)
            {
                // template 100, flags 0b10011
                BinaryPrimitives.WriteUInt16LittleEndian(
                    dataSet.AsSpan(512 + row * 2), (ushort)((100 << 5) | 0b10011));
            }
        }

        var doc = new MtrlDocument
        {
            Version = 0x01030000,
            ShaderPack = "character.shpk",
            Textures =
            [
                new MtrlDocument.MtrlTextureRef { Path = NormalGamePath, Flags = 0 },
                new MtrlDocument.MtrlTextureRef { Path = MaskGamePath, Flags = 0 },
            ],
            ColorSets = [new MtrlDocument.MtrlNamedSet { Name = "colorset", Index = 0 }],
            AdditionalData = [0x04, 0x00, 0x00, 0x00],
            DataSet = dataSet,
            ShaderKeys = [new MtrlDocument.MtrlShaderKey { Category = 0xB616DC5A, Value = 0x1DF2985C }],
            Constants = [new MtrlDocument.MtrlConstant { Id = 0x11223344, Offset = 0, Size = 4 }],
            Samplers =
            [
                new MtrlDocument.MtrlSampler { SamplerId = DawntrailUpgrade.SamplerNormalId, Settings = 0x000F8000, TextureIndex = 0 },
                new MtrlDocument.MtrlSampler { SamplerId = DawntrailUpgrade.SamplerMaskId, Settings = 0x000F8340, TextureIndex = 1 },
            ],
            Flags1 = 0x0011,
            Flags2 = 0x0100,
            ShaderValues = [1, 2, 3, 4],
        };
        return doc.Write();
    }

    // --- MtrlDocument ---

    [Fact]
    public void MtrlDocumentRoundTripsByteExact()
    {
        var original = BuildLegacyMaterial(withDye: true);
        var reparsed = MtrlDocument.Parse(original);
        Assert.Equal(original, reparsed.Write());
        Assert.Equal("character.shpk", reparsed.ShaderPack);
        Assert.Equal([NormalGamePath, MaskGamePath], reparsed.Textures.Select(t => t.Path));

        // The simple parser agrees about the legacy table.
        var simple = MtrlParser.Parse(original);
        Assert.Equal(16, simple.ColorTable.Count);
        Assert.Equal(32f, simple.ColorTable[0].Gloss);
        Assert.Equal(0.75f, simple.ColorTable[0].SpecularStrength);
    }

    // --- Material conversion ---

    [Fact]
    public void UpgradeProducesADawntrailCharacterLegacyMaterial()
    {
        var result = DawntrailUpgrade.UpgradeCharacterMaterial(BuildLegacyMaterial(withDye: true));

        Assert.Equal(NormalGamePath, result.NormalPath);
        Assert.Equal(MaskGamePath, result.MaskPath);
        Assert.Equal(NormalGamePath.Replace("_n.tex", "_id.tex"), result.IndexPath);

        var doc = MtrlDocument.Parse(result.Data);
        Assert.Equal("characterlegacy.shpk", doc.ShaderPack);
        Assert.Equal([0x34, 0x05, 0x00, 0x00], doc.AdditionalData);
        Assert.Equal(2048 + 128, doc.DataSet.Length);

        // The index texture arrived with its sampler wired to it.
        Assert.Equal(result.IndexPath, doc.Textures[^1].Path);
        var indexSampler = doc.Samplers.Single(s => s.SamplerId == DawntrailUpgrade.SamplerIndexId);
        Assert.Equal(doc.Textures.Count - 1, indexSampler.TextureIndex);

        // The regular parser reads the upgraded table with Dawntrail scalar slots.
        var parsed = MtrlParser.Parse(result.Data);
        Assert.Equal(32, parsed.ColorTable.Count);
        var row = parsed.ColorTable[0];
        Assert.Equal(0.5f, row.Diffuse.X);
        Assert.Equal(0.25f, row.Diffuse.Y);
        Assert.Equal(32f, row.Gloss);
        Assert.Equal(0.75f, row.SpecularStrength);
        Assert.Equal(2f, row.Emissive.X);
        // Rows 16-31 are the standard default rows (white diffuse).
        Assert.Equal(1f, parsed.ColorTable[16].Diffuse.X);

        // Field relocations: subsurface id 3 → half 25, alpha 1 → half 26, tiling → halfs 28-31.
        var row0 = doc.DataSet;
        Assert.Equal(3f, ReadHalf(row0, 25));
        Assert.Equal(1f, ReadHalf(row0, 26));
        Assert.Equal(4f, ReadHalf(row0, 28));
        Assert.Equal(16f, ReadHalf(row0, 31));

        // Dye: 2-byte legacy entries became 4-byte Dawntrail ones (template << 16 | flags).
        var dye = BinaryPrimitives.ReadUInt32LittleEndian(doc.DataSet.AsSpan(2048));
        Assert.Equal((uint)((100 << 16) | 0b10011), dye);
    }

    [Fact]
    public void UpgradeRefusesNonLegacyMaterials()
    {
        var upgraded = DawntrailUpgrade.UpgradeCharacterMaterial(BuildLegacyMaterial(withDye: false)).Data;
        Assert.Throws<InvalidDataException>(() => DawntrailUpgrade.UpgradeCharacterMaterial(upgraded));
    }

    // --- Texture math ---

    [Fact]
    public void IndexPixelsFollowTheReferenceMath()
    {
        // Normal alpha held the legacy color-set row selector.
        byte[] normal =
        [
            0, 0, 0, 0,     // row 0 exactly       → pair 0 (r 4),  g 255
            0, 0, 0, 17,    // row 1 exactly       → pair 0 (r 4),  g 0
            0, 0, 0, 34,    // row 2 exactly       → pair 1 (r 21), g 255
            0, 0, 0, 255,   // row 15              → pair 7 (r 123), g 0
        ];
        var index = DawntrailUpgrade.CreateIndexRgba(normal);

        Assert.Equal([4, 255, 0, 255], index[..4]);
        Assert.Equal([4, 0, 0, 255], index[4..8]);
        Assert.Equal([21, 255, 0, 255], index[8..12]);
        Assert.Equal([123, 0, 0, 255], index[12..16]);
    }

    [Fact]
    public void MaskAndNormalChannelConversions()
    {
        byte[] mask = [10, 20, 30, 40];
        DawntrailUpgrade.ConvertLegacyMaskRgba(mask);
        Assert.Equal([30, 20, 10, 40], mask); // spec, gloss, occlusion

        byte[] normal = [1, 2, 3, 200];
        DawntrailUpgrade.ConvertLegacyNormalRgba(normal);
        Assert.Equal([1, 2, 3, 3], normal); // opacity moves from blue to alpha
    }

    // --- Whole-mod flow ---

    private string BuildMod()
    {
        var dir = Path.Combine(_root, "mod");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "gear.mtrl"), BuildLegacyMaterial(withDye: false));

        // 2x2 normal: alpha = row selector, blue = opacity.
        var normalRgba = new byte[]
        {
            128, 128, 255, 17, 128, 128, 255, 17,
            128, 128, 200, 34, 128, 128, 255, 0,
        };
        File.WriteAllBytes(Path.Combine(dir, "gear_n.tex"), TexWriter.Write(2, 2, normalRgba));

        var maskRgba = new byte[] { 10, 20, 30, 255, 11, 21, 31, 255, 12, 22, 32, 255, 13, 23, 33, 255 };
        File.WriteAllBytes(Path.Combine(dir, "gear_m.tex"), TexWriter.Write(2, 2, maskRgba));

        var meta = new
        {
            FileVersion = 4,
            Name = "Legacy Gear Mod",
            DefaultData = new
            {
                Files = new Dictionary<string, string>
                {
                    [MtrlGamePath] = "gear.mtrl",
                    [NormalGamePath] = "gear_n.tex",
                    [MaskGamePath] = "gear_m.tex",
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
        return dir;
    }

    private static DawntrailModUpgrader CreateUpgrader() => new(
        new PenumbraLinkService(NullLogger<PenumbraLinkService>.Instance),
        new LuminaGameDataService(NullLogger<LuminaGameDataService>.Instance),
        NullLogger<DawntrailModUpgrader>.Instance);

    // --- Modpack (file → file) flow ---

    [Fact]
    public async Task PmpModpackUpgradesIntoANewPmp()
    {
        var modDir = BuildMod();
        var inputPmp = Path.Combine(_root, "legacy.pmp");
        System.IO.Compression.ZipFile.CreateFromDirectory(modDir, inputPmp);
        var inputBytes = File.ReadAllBytes(inputPmp);
        var outputPmp = Path.Combine(_root, "legacy (DT).pmp");

        var report = await CreateUpgrader().UpgradeModpackAsync(inputPmp, outputPmp);

        Assert.Equal(1, report.MaterialsUpgraded);
        Assert.Equal(outputPmp, report.OutputPath);
        Assert.Equal(inputBytes, File.ReadAllBytes(inputPmp)); // the input is untouched

        using var zip = System.IO.Compression.ZipFile.OpenRead(outputPmp);
        Assert.NotNull(zip.GetEntry("meta.json"));
        Assert.NotNull(zip.GetEntry("gear_id.tex"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(".moonlace-backup"));

        using var mtrlStream = new MemoryStream();
        zip.GetEntry("gear.mtrl")!.Open().CopyTo(mtrlStream);
        Assert.Equal("characterlegacy.shpk", MtrlParser.Parse(mtrlStream.ToArray()).ShaderPack);
    }

    /// <summary>A single-block uncompressed SqPack type-2 entry, the format TTMPD.mpd stores files in.</summary>
    private static byte[] SqPackType2(byte[] data)
    {
        const int headerSize = 128;
        var result = new byte[headerSize + 16 + data.Length];
        var span = result.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, headerSize);              // header size
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);                  // FileType.Standard
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)data.Length);  // raw size
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);                 // block count
        // Block info: offset 0, compressed/uncompressed sizes.
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)(16 + data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)data.Length);
        // Block header: size 16, type 32000 (uncompressed), data size.
        BinaryPrimitives.WriteUInt32LittleEndian(span[headerSize..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(headerSize + 8)..], 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(headerSize + 12)..], (uint)data.Length);
        data.CopyTo(span[(headerSize + 16)..]);
        return result;
    }

    /// <summary>
    /// A SqPack type-4 texture entry laid out like TexTools writes them: the
    /// .tex header stored raw, then one deflated block per mip. The last
    /// block's header can overstate its decompressed size, as in "L ⬦
    /// Macaron" (its 1×1 mips claim 8 bytes and inflate to 4).
    /// </summary>
    private static byte[] SqPackType4(byte[] tex, int[] mipSizes, int overstateLastBlock = 0)
    {
        const int texHeaderSize = 80;
        var blocks = new List<byte[]>();
        var mipOffset = texHeaderSize;
        for (var i = 0; i < mipSizes.Length; i++)
        {
            var compressed = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(
                       compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(tex, mipOffset, mipSizes[i]);
            mipOffset += mipSizes[i];

            var claimed = mipSizes[i] + (i == mipSizes.Length - 1 ? overstateLastBlock : 0);
            var block = new byte[(16 + (int)compressed.Length + 127) / 128 * 128];
            BinaryPrimitives.WriteUInt32LittleEndian(block, 16);                                // block header size
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), (uint)compressed.Length); // compressed size
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)claimed);         // decompressed size
            compressed.ToArray().CopyTo(block, 16);
            blocks.Add(block);
        }

        var lodCount = mipSizes.Length;
        var headerSize = (24 + lodCount * 22 + 127) / 128 * 128;
        var header = new byte[headerSize];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 4);                // FileType.Texture
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)tex.Length); // raw size
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)lodCount);
        var compressedOffset = texHeaderSize;
        for (var i = 0; i < lodCount; i++)
        {
            // LOD block: compressed offset, compressed size, decompressed size, first block, block count.
            var lod = span[(24 + i * 20)..];
            BinaryPrimitives.WriteUInt32LittleEndian(lod, (uint)compressedOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(lod[4..], (uint)blocks[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(lod[8..], (uint)mipSizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(lod[12..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(lod[16..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(24 + lodCount * 20 + i * 2)..], (ushort)blocks[i].Length);
            compressedOffset += blocks[i].Length;
        }

        var result = new MemoryStream();
        result.Write(header);
        result.Write(tex, 0, texHeaderSize);
        foreach (var block in blocks)
            result.Write(block);
        return result.ToArray();
    }

    [Fact]
    public void TtmpdTextureEntriesSurviveAnOverstatedLastBlock()
    {
        var rgba = Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)(i * 3)).ToArray();
        var tex = TexWriter.Write(4, 4, rgba);
        var entry = SqPackType4(tex, [64, 16, 4], overstateLastBlock: 4);

        using var mpd = new MemoryStream(entry);
        using var sqpack = new SqPackStream(mpd, PlatformId.Win32);

        // The fixture reproduces the real failure: Lumina trusts the block
        // header and overruns its raw-size buffer.
        Assert.ThrowsAny<Exception>(() => sqpack.ReadFile<FileResource>(0));
        Assert.Equal(tex, TtmpdEntryReader.Read(mpd, sqpack, 0));
    }

    /// <summary>A minimal TexTools .meta blob holding a single GMP chunk (visor enabled).</summary>
    private static byte[] MetaBlobWithGmp(string targetPath)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2u); // metadata version
        writer.Write(System.Text.Encoding.UTF8.GetBytes(targetPath));
        writer.Write((byte)0);
        writer.Write(1u);  // one chunk
        writer.Write(12u); // per-chunk header size
        var headerStart = (uint)stream.Position + 4;
        writer.Write(headerStart);
        writer.Write(5u); // chunk type Gmp
        writer.Write(headerStart + 12); // chunk offset
        writer.Write(5u); // chunk size
        writer.Write([0x01, 0x00, 0x00, 0x00, 0x00]); // enabled, no rotation
        return stream.ToArray();
    }

    [Fact]
    public async Task TtmpModpackConvertsAndUpgradesIntoAPmp()
    {
        // Build a .ttmp2 modpack: TTMPL.mpl manifest + TTMPD.mpd data blob.
        var mtrlBlob = SqPackType2(BuildLegacyMaterial(withDye: false));
        var normalBlob = SqPackType2(TexWriter.Write(2, 2, new byte[]
        {
            128, 128, 255, 17, 128, 128, 255, 17,
            128, 128, 255, 34, 128, 128, 255, 0,
        }));
        var maskBlob = SqPackType2(TexWriter.Write(2, 2, new byte[]
        {
            10, 20, 30, 255, 11, 21, 31, 255, 12, 22, 32, 255, 13, 23, 33, 255,
        }));
        var metaBlob = SqPackType2(MetaBlobWithGmp("chara/equipment/e9999/e9999_met.meta"));

        var mpd = new MemoryStream();
        long mtrlOffset = mpd.Position;
        mpd.Write(mtrlBlob);
        long normalOffset = mpd.Position;
        mpd.Write(normalBlob);
        long maskOffset = mpd.Position;
        mpd.Write(maskBlob);
        long metaOffset = mpd.Position;
        mpd.Write(metaBlob);

        var manifest = new
        {
            TTMPVersion = "2.0",
            Name = "Legacy TT Mod",
            Author = "Tester",
            Version = "1.0.0",
            SimpleModsList = new object[]
            {
                new { Name = "Material", FullPath = MtrlGamePath, ModOffset = mtrlOffset, ModSize = mtrlBlob.Length },
                new { Name = "Normal", FullPath = NormalGamePath, ModOffset = normalOffset, ModSize = normalBlob.Length },
                new { Name = "Mask", FullPath = MaskGamePath, ModOffset = maskOffset, ModSize = maskBlob.Length },
                new { Name = "Meta", FullPath = "chara/equipment/e9999/e9999_met.meta", ModOffset = metaOffset, ModSize = metaBlob.Length },
            },
        };

        Directory.CreateDirectory(_root);
        var ttmpPath = Path.Combine(_root, "legacy.ttmp2");
        using (var stream = File.Create(ttmpPath))
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("TTMPL.mpl").Open()))
                writer.Write(JsonSerializer.Serialize(manifest));
            using (var data = zip.CreateEntry("TTMPD.mpd").Open())
                data.Write(mpd.ToArray());
        }

        var outputPmp = Path.Combine(_root, "legacy-tt (DT).pmp");
        var report = await CreateUpgrader().UpgradeModpackAsync(ttmpPath, outputPmp);

        Assert.Equal(1, report.MaterialsUpgraded);
        Assert.Equal(1, report.IndexTexturesCreated);
        Assert.Equal(1, report.MasksConverted);
        Assert.DoesNotContain(report.Warnings, w => w.Contains(".meta"));

        using var pmp = System.IO.Compression.ZipFile.OpenRead(outputPmp);
        Assert.NotNull(pmp.GetEntry("meta.json"));

        // The default file redirections carry the game paths, including the new index texture.
        using var defaultModStream = new MemoryStream();
        pmp.GetEntry("default_mod.json")!.Open().CopyTo(defaultModStream);
        var defaultMod = JsonDocument.Parse(defaultModStream.ToArray()).RootElement;
        var files = defaultMod.GetProperty("Files");
        Assert.True(files.TryGetProperty(MtrlGamePath, out _));
        Assert.True(files.TryGetProperty(NormalGamePath.Replace("_n.tex", "_id.tex"), out _));
        Assert.False(files.TryGetProperty("chara/equipment/e9999/e9999_met.meta", out _));

        // The .meta blob rides along as a Penumbra manipulation instead.
        var manipulation = defaultMod.GetProperty("Manipulations").EnumerateArray().Single();
        Assert.Equal("Gmp", manipulation.GetProperty("Type").GetString());
        Assert.Equal(9999, manipulation.GetProperty("Manipulation").GetProperty("SetId").GetInt32());
        Assert.True(manipulation.GetProperty("Manipulation").GetProperty("Entry").GetProperty("Enabled").GetBoolean());

        // The extracted-and-upgraded material is characterlegacy.
        var mtrlEntry = pmp.Entries.Single(e => e.FullName.EndsWith(".mtrl", StringComparison.Ordinal));
        using var mtrlStream = new MemoryStream();
        mtrlEntry.Open().CopyTo(mtrlStream);
        Assert.Equal("characterlegacy.shpk", MtrlParser.Parse(mtrlStream.ToArray()).ShaderPack);
    }

    /// <summary>Writes a .ttmp2 modpack: TTMPL.mpl manifest + TTMPD.mpd data blob.</summary>
    private static void WriteTtmp(string path, object manifest, byte[] mpd)
    {
        using var stream = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("TTMPL.mpl").Open()))
            writer.Write(JsonSerializer.Serialize(manifest));
        using (var data = zip.CreateEntry("TTMPD.mpd").Open())
            data.Write(mpd);
    }

    [Fact]
    public async Task TtmpTexturesWithOverstatedBlocksSurviveTheUpgrade()
    {
        // Normal and mask stored the way "L ⬦ Macaron" stores its big textures.
        var normalTex = TexWriter.Write(2, 2, new byte[]
        {
            128, 128, 255, 17, 128, 128, 255, 17,
            128, 128, 255, 34, 128, 128, 255, 0,
        });
        var maskTex = TexWriter.Write(2, 2, new byte[]
        {
            10, 20, 30, 255, 11, 21, 31, 255, 12, 22, 32, 255, 13, 23, 33, 255,
        });

        var mpd = new MemoryStream();
        long mtrlOffset = mpd.Position;
        mpd.Write(SqPackType2(BuildLegacyMaterial(withDye: false)));
        long normalOffset = mpd.Position;
        mpd.Write(SqPackType4(normalTex, [16, 4], overstateLastBlock: 4));
        long maskOffset = mpd.Position;
        mpd.Write(SqPackType4(maskTex, [16, 4], overstateLastBlock: 4));

        var manifest = new
        {
            TTMPVersion = "1.3w",
            Name = "Big TexTools Textures",
            SimpleModsList = new object[]
            {
                new { Name = "Material", FullPath = MtrlGamePath, ModOffset = mtrlOffset },
                new { Name = "Normal", FullPath = NormalGamePath, ModOffset = normalOffset },
                new { Name = "Mask", FullPath = MaskGamePath, ModOffset = maskOffset },
            },
        };

        Directory.CreateDirectory(_root);
        var ttmpPath = Path.Combine(_root, "big-textures.ttmp2");
        WriteTtmp(ttmpPath, manifest, mpd.ToArray());

        var outputPmp = Path.Combine(_root, "big-textures (DT).pmp");
        var report = await CreateUpgrader().UpgradeModpackAsync(ttmpPath, outputPmp);

        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.MaterialsUpgraded);
        Assert.Equal(1, report.IndexTexturesCreated);
        Assert.Equal(1, report.NormalsConverted);
        Assert.Equal(1, report.MasksConverted);

        // Both textures made it into the package, converted.
        using var pmp = System.IO.Compression.ZipFile.OpenRead(outputPmp);
        byte[] Entry(string suffix)
        {
            using var stream = new MemoryStream();
            pmp.Entries.Single(e => e.FullName.EndsWith(suffix, StringComparison.Ordinal)).Open().CopyTo(stream);
            return stream.ToArray();
        }

        Assert.Equal([30, 20, 10, 255], TexWriter.TryReadB8G8R8A8(Entry("_m.tex"))!.Value.Rgba[..4]);
        Assert.Equal(255, TexWriter.TryReadB8G8R8A8(Entry("_n.tex"))!.Value.Rgba[3]);
        Assert.Equal([4, 0, 0, 255], TexWriter.TryReadB8G8R8A8(Entry("_id.tex"))!.Value.Rgba[..4]);
    }

    [Fact]
    public async Task NormalVariantsInOtherOptionsEachGetTheirOwnIndexMap()
    {
        // Shaped like "L ⬦ Macaron": the material lives in a dye option, the
        // normal it uses is swapped by a separate ruffles group.
        var dir = Path.Combine(_root, "options-mod");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "dye.mtrl"), BuildLegacyMaterial(withDye: false));
        File.WriteAllBytes(Path.Combine(dir, "with_n.tex"), TexWriter.Write(1, 1, [128, 128, 255, 17]));
        File.WriteAllBytes(Path.Combine(dir, "without_n.tex"), TexWriter.Write(1, 1, [128, 128, 0, 34]));

        var meta = new
        {
            FileVersion = 4,
            Name = "Option Normals",
            Groups = new object[]
            {
                new
                {
                    Name = "Dye", Type = "Single",
                    Options = new[] { new { Name = "White", Files = new Dictionary<string, string> { [MtrlGamePath] = "dye.mtrl" } } },
                },
                new
                {
                    Name = "Ruffles", Type = "Single",
                    Options = new[]
                    {
                        new { Name = "With", Files = new Dictionary<string, string> { [NormalGamePath] = "with_n.tex" } },
                        new { Name = "Without", Files = new Dictionary<string, string> { [NormalGamePath] = "without_n.tex" } },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));

        var report = await CreateUpgrader().UpgradeAsync(dir);
        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.MaterialsUpgraded);
        Assert.Equal(2, report.NormalsConverted);
        Assert.Equal(2, report.IndexTexturesCreated);

        // Each variant got its own index map from its own row data and its own opacity in alpha.
        byte[] Read(string file) => TexWriter.TryReadB8G8R8A8(File.ReadAllBytes(Path.Combine(dir, file)))!.Value.Rgba;
        Assert.Equal([4, 0, 0, 255], Read("with_id.tex"));     // row 1 → pair 0, odd row
        Assert.Equal([21, 255, 0, 255], Read("without_id.tex")); // row 2 → pair 1, even row
        Assert.Equal(255, Read("with_n.tex")[3]);
        Assert.Equal(0, Read("without_n.tex")[3]);

        // The index switches together with its normal: registered in each
        // ruffles option, not in the material's dye option.
        var indexPath = NormalGamePath.Replace("_n.tex", "_id.tex");
        var groups = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json"))).RootElement.GetProperty("Groups");
        JsonElement Files(int group, int option) =>
            groups[group].GetProperty("Options")[option].GetProperty("Files");
        Assert.False(Files(0, 0).TryGetProperty(indexPath, out _));
        Assert.Equal("with_id.tex", Files(1, 0).GetProperty(indexPath).GetString());
        Assert.Equal("without_id.tex", Files(1, 1).GetProperty(indexPath).GetString());
    }

    [Fact]
    public async Task UpgradesAWholeModAndRevertRestoresIt()
    {
        var dir = BuildMod();
        var originalMtrl = File.ReadAllBytes(Path.Combine(dir, "gear.mtrl"));

        var report = await CreateUpgrader().UpgradeAsync(dir);
        Assert.Equal(1, report.MaterialsUpgraded);
        Assert.Equal(1, report.MasksConverted);
        Assert.Equal(1, report.NormalsConverted);
        Assert.Equal(1, report.IndexTexturesCreated);
        Assert.Empty(report.Warnings);

        // The material on disk is characterlegacy now and the index redirection is registered.
        Assert.Equal("characterlegacy.shpk", MtrlParser.Parse(File.ReadAllBytes(Path.Combine(dir, "gear.mtrl"))).ShaderPack);
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        var files = meta.RootElement.GetProperty("DefaultData").GetProperty("Files");
        Assert.Equal("gear_id.tex", files.GetProperty(NormalGamePath.Replace("_n.tex", "_id.tex")).GetString());

        // Index pixels follow the math (alpha 17 → row 1 → pair 0, blend 0).
        var index = TexWriter.TryReadB8G8R8A8(File.ReadAllBytes(Path.Combine(dir, "gear_id.tex")))!.Value;
        Assert.Equal(2, index.Width);
        Assert.Equal([4, 0, 0, 255], index.Rgba[..4]);

        // Mask channels shuffled, normal alpha = old blue.
        var mask = TexWriter.TryReadB8G8R8A8(File.ReadAllBytes(Path.Combine(dir, "gear_m.tex")))!.Value;
        Assert.Equal([30, 20, 10, 255], mask.Rgba[..4]);
        var normal = TexWriter.TryReadB8G8R8A8(File.ReadAllBytes(Path.Combine(dir, "gear_n.tex")))!.Value;
        Assert.Equal(255, normal.Rgba[3]);
        Assert.Equal(200, normal.Rgba[11]);

        // A second run is a no-op: everything already current.
        var again = await CreateUpgrader().UpgradeAsync(dir);
        Assert.Equal(0, again.MaterialsUpgraded);
        Assert.Equal(1, again.AlreadyCurrent);

        // The upgrade shares the live-edit backup store: linking + revert restores everything.
        var link = new PenumbraLinkService(NullLogger<PenumbraLinkService>.Instance);
        link.Link(dir, []);
        Assert.True(link.ChangedFileCount > 0);
        link.RevertAll();

        Assert.Equal(originalMtrl, File.ReadAllBytes(Path.Combine(dir, "gear.mtrl")));
        Assert.False(File.Exists(Path.Combine(dir, "gear_id.tex")));
        var reverted = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        Assert.False(reverted.RootElement.GetProperty("DefaultData").GetProperty("Files")
            .TryGetProperty(NormalGamePath.Replace("_n.tex", "_id.tex"), out _));
    }
}
