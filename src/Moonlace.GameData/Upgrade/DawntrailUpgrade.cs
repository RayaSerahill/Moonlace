using System.Buffers.Binary;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Upgrade;

/// <summary>
/// Pure Endwalker → Dawntrail conversions, verified against vanilla
/// Dawntrail "characterlegacy" materials. (Conversion logic ported from
/// xivModdingFramework's EndwalkerUpgrade/TextureHelpers — kept as the
/// single attribution note.)
///
/// - Legacy gear materials (character.shpk with a 16-row color set) become
///   characterlegacy.shpk with a 32-row color set (scalar slots swap: legacy
///   gloss half[7] → DT half[3], legacy specular power half[3] → DT half[7]),
///   Dawntrail dye data, additional data 0x0534, and an added index texture
///   + g_SamplerIndex sampler.
/// - The index texture is generated from the legacy normal map's alpha
///   channel (which used to select the color-set row).
/// - Legacy gear masks shuffle channels: DT red = old specular (blue),
///   DT green = old gloss (green), DT blue = old occlusion (red).
/// - Normal maps move opacity from blue (legacy) into alpha (Dawntrail),
///   which also frees alpha of the stale row-index data.
/// </summary>
public static class DawntrailUpgrade
{
    internal const uint SamplerNormalId = 0x0C5EC1F1;  // g_SamplerNormal
    internal const uint SamplerMaskId = 0x8A4E82B6;    // g_SamplerMask
    internal const uint SamplerIndexId = 0x565F8FD8;   // g_SamplerIndex

    /// <summary>Shader key marking materials that use the mask slot as a specular map — their mask must not be converted.</summary>
    private const uint MaskAsSpecularKey = 0xC8BD1DEF;
    private static readonly uint[] MaskAsSpecularValues = [0xA02F4828, 0x198D11CD];

    public sealed class MaterialResult
    {
        public required byte[] Data { get; init; }

        /// <summary>Game path of the normal texture the material references (source for the index map).</summary>
        public string? NormalPath { get; init; }

        /// <summary>Game path of the mask to convert, or null (absent, or used as a specular map).</summary>
        public string? MaskPath { get; init; }

        /// <summary>Game path the new index texture reference points at (null when no normal exists).</summary>
        public string? IndexPath { get; init; }
    }

    /// <summary>True for materials the gear upgrade applies to: character.shpk with a legacy 16-row color set.</summary>
    public static bool IsLegacyCharacterMaterial(MtrlDocument doc) =>
        doc.ShaderPack == "character.shpk" && doc.DataSet.Length is 512 or 544;

    /// <summary>
    /// Upgrades a legacy gear material. <paramref name="vanillaIndexPath"/>
    /// supplies the game's own index texture path when the caller knows it
    /// (vanilla-referenced normals were upgraded by the game itself).
    /// </summary>
    public static MaterialResult UpgradeCharacterMaterial(byte[] original, string? vanillaIndexPath = null)
    {
        var doc = MtrlDocument.Parse(original);
        if (!IsLegacyCharacterMaterial(doc))
            throw new InvalidDataException(
                $"Not a legacy gear material (shader {doc.ShaderPack}, color set {doc.DataSet.Length} bytes).");

        doc.ShaderPack = "characterlegacy.shpk";
        doc.AdditionalData = [0x34, 0x05, 0x00, 0x00];

        // DX9 texture variants are gone; the flag confuses current tooling.
        foreach (var texture in doc.Textures)
            texture.Flags &= unchecked((ushort)~0x8000);

        doc.DataSet = ConvertColorSet(doc.DataSet);

        var normal = FindTexture(doc, SamplerNormalId);
        string? indexPath = null;
        if (normal is not null)
        {
            indexPath = vanillaIndexPath ?? DeriveIndexPath(normal.Path);
            doc.Textures.Add(new MtrlDocument.MtrlTextureRef { Path = indexPath, Flags = 0 });
            var normalSampler = doc.Samplers.First(s => s.SamplerId == SamplerNormalId);
            doc.Samplers.Add(new MtrlDocument.MtrlSampler
            {
                SamplerId = SamplerIndexId,
                Settings = normalSampler.Settings,
                TextureIndex = (byte)(doc.Textures.Count - 1),
            });
        }

        var maskIsSpecular = doc.ShaderKeys.Any(k =>
            k.Category == MaskAsSpecularKey && MaskAsSpecularValues.Contains(k.Value));
        var mask = FindTexture(doc, SamplerMaskId);

        return new MaterialResult
        {
            Data = doc.Write(),
            NormalPath = normal?.Path,
            MaskPath = maskIsSpecular ? null : mask?.Path,
            IndexPath = indexPath,
        };
    }

    public static string DeriveIndexPath(string normalPath) =>
        normalPath.Contains("_n.tex", StringComparison.Ordinal)
            ? normalPath.Replace("_n.tex", "_id.tex", StringComparison.Ordinal)
            : normalPath.Replace(".tex", "_id.tex", StringComparison.Ordinal);

    private static MtrlDocument.MtrlTextureRef? FindTexture(MtrlDocument doc, uint samplerId)
    {
        var sampler = doc.Samplers.FirstOrDefault(s => s.SamplerId == samplerId);
        if (sampler is null || sampler.TextureIndex >= doc.Textures.Count)
            return null;
        return doc.Textures[sampler.TextureIndex];
    }

    // --- Color set ---

    /// <summary>
    /// 512-byte (16 rows × 16 halfs) legacy tables become 2048-byte (32 rows
    /// × 32 halfs) Dawntrail tables: legacy row i maps onto new row i (rows
    /// 16-31 stay defaults), gloss and specular power swap scalar slots, and
    /// the tile/subsurface fields move to their new offsets. A trailing
    /// 32-byte legacy dye block becomes the 128-byte Dawntrail one.
    /// </summary>
    private static byte[] ConvertColorSet(byte[] dataSet)
    {
        var hasDye = dataSet.Length == 544;
        var result = new byte[hasDye ? 2048 + 128 : 2048];

        // Rows start from the standard Dawntrail default row.
        var defaultRow = DefaultRowBytes();
        for (var i = 0; i < 32; i++)
            defaultRow.CopyTo(result.AsSpan(i * 64));

        void CopyHalf(int oldHalf, int newHalf, int row) =>
            dataSet.AsSpan((row * 16 + oldHalf) * 2, 2).CopyTo(result.AsSpan((row * 32 + newHalf) * 2, 2));

        for (var row = 0; row < 16; row++)
        {
            for (var c = 0; c < 3; c++)
            {
                CopyHalf(0 + c, 0 + c, row);   // diffuse
                CopyHalf(4 + c, 4 + c, row);   // specular
                CopyHalf(8 + c, 8 + c, row);   // emissive
            }

            CopyHalf(7, 3, row);               // gloss  (SE swapped the scalar slots)
            CopyHalf(3, 7, row);               // specular power
            CopyHalf(11, 25, row);             // subsurface material id
            WriteHalfOne(result, row * 32 + 26); // subsurface alpha = 1.0
            for (var c = 0; c < 4; c++)
                CopyHalf(12 + c, 28 + c, row); // tile / subsurface scaling
        }

        if (hasDye)
        {
            for (var row = 0; row < 16; row++)
            {
                var old = BinaryPrimitives.ReadUInt16LittleEndian(dataSet.AsSpan(512 + row * 2));
                var dyeBits = (uint)(old & 0x1F);
                var template = (uint)(old >> 5);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2048 + row * 4), (template << 16) | dyeBits);
            }
        }

        return result;
    }

    private static void WriteHalfOne(byte[] buffer, int halfIndex) =>
        BinaryPrimitives.WriteHalfLittleEndian(buffer.AsSpan(halfIndex * 2), (Half)1.0f);

    private static byte[] DefaultRowBytes()
    {
        var halves = new float[32];
        for (var i = 0; i < 8; i++)
            halves[i] = 1.0f;
        halves[26] = 1.0f;
        halves[28] = 16.0f;
        halves[31] = 16.0f;

        var bytes = new byte[64];
        for (var i = 0; i < 32; i++)
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)halves[i]);
        return bytes;
    }

    // --- Textures (RGBA8 pixel buffers) ---

    /// <summary>
    /// Builds Dawntrail index-map pixels from a legacy normal map: the old
    /// alpha selected the color-set row (steps of 17) with fractional
    /// blending; the index map's red picks the row pair and green blends
    /// inside it.
    /// </summary>
    public static byte[] CreateIndexRgba(ReadOnlySpan<byte> normalRgba)
    {
        var index = new byte[normalRgba.Length];
        for (var offset = 0; offset < normalRgba.Length; offset += 4)
        {
            int originalCset = normalRgba[offset + 3];
            var blendRem = originalCset % 34;
            var originalRow = originalCset / 17;

            if (blendRem > 17)
            {
                if (blendRem < 26)
                {
                    blendRem = 17;      // clamp to the closer row
                }
                else
                {
                    blendRem = 0;       // next row is closer
                    originalRow++;
                }
            }

            var newBlend = (byte)(255 - Math.Round(blendRem / 17.0f * 255.0f));
            // Push slightly into the row so BC5 compression cannot bleed across rows.
            var newRow = (byte)(originalRow / 2 * 17 + 4);

            index[offset + 0] = newRow;
            index[offset + 1] = newBlend;
            index[offset + 2] = 0;
            index[offset + 3] = 255;
        }

        return index;
    }

    /// <summary>
    /// Legacy gear mask (R = occlusion, G = gloss, B = specular) → Dawntrail
    /// characterlegacy mask (R = specular, G = gloss, B = occlusion/diffuse).
    /// </summary>
    public static void ConvertLegacyMaskRgba(Span<byte> rgba)
    {
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            var ao = rgba[offset + 0];
            var gloss = rgba[offset + 1];
            var spec = rgba[offset + 2];

            rgba[offset + 0] = spec;
            rgba[offset + 1] = gloss;
            rgba[offset + 2] = ao;
        }
    }

    /// <summary>
    /// Legacy normals carried opacity in blue and the color-set row in alpha;
    /// Dawntrail reads opacity from alpha. The row data just moved into the
    /// index map, so alpha becomes the old blue.
    /// </summary>
    public static void ConvertLegacyNormalRgba(Span<byte> rgba)
    {
        for (var offset = 0; offset < rgba.Length; offset += 4)
            rgba[offset + 3] = rgba[offset + 2];
    }
}
