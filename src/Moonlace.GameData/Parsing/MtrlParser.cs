using System.Numerics;
using System.Text;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Moonlace-owned parser for FFXIV .mtrl files.
///
/// Layout follows Lumina's MIT-licensed MtrlStructs documentation. Lumina's
/// own MtrlFile assumes the pre-Dawntrail 512-byte color table; Dawntrail
/// materials carry a 32-row × 64-byte table, which this parser handles by
/// sizing the table from the header's data-set size.
/// </summary>
public static class MtrlParser
{
    public static ParsedMaterial Parse(byte[] data)
    {
        var r = new SpanReader(data);

        r.ReadUInt32(); // version
        var fileAndDataSetSize = r.ReadUInt32();
        var dataSetSize = (int)(fileAndDataSetSize >> 16);
        int stringTableSize = r.ReadUInt16();
        int shaderPackageNameOffset = r.ReadUInt16();
        int textureCount = r.ReadByte();
        int uvSetCount = r.ReadByte();
        int colorSetCount = r.ReadByte();
        int additionalDataSize = r.ReadByte();

        var textureOffsets = new int[textureCount];
        for (var i = 0; i < textureCount; i++)
            textureOffsets[i] = (ushort)r.ReadUInt32(); // high 16 bits are flags

        r.Skip(uvSetCount * 4);
        r.Skip(colorSetCount * 4);

        var strings = r.ReadBytes(stringTableSize).ToArray();
        r.Skip(additionalDataSize);

        var colorTableOffset = r.Position;
        var colorTable = ParseColorTable(ref r, dataSetSize);

        var texturePaths = new string[textureCount];
        for (var i = 0; i < textureCount; i++)
            texturePaths[i] = ReadCString(strings, textureOffsets[i]);

        return new ParsedMaterial
        {
            ShaderPack = ReadCString(strings, shaderPackageNameOffset),
            TexturePaths = texturePaths,
            ColorTable = colorTable,
            ColorTableOffset = colorTable.Length > 0 ? colorTableOffset : -1,
        };
    }

    /// <summary>
    /// The data set holds the color table (and optionally dye data after it).
    /// Legacy table: 16 rows × 32 bytes (16 halfs). Dawntrail: 32 rows × 64
    /// bytes (32 halfs). Shared row layout: diffuse RGB at halfs 0-2,
    /// specular RGB at 4-6, emissive RGB at 8-10. The two scalars swapped
    /// places in Dawntrail: legacy has specular strength at 3 and gloss at 7;
    /// Dawntrail rows carry the gloss (shininess) exponent at 3 and the
    /// specular strength at 7 (verified against real 7.x materials, where
    /// slot 3 holds values like 20/32 and slot 7 sits at 1.0).
    /// </summary>
    internal static (int Rows, int HalfsPerRow) TableShape(int dataSetSize) => dataSetSize switch
    {
        >= 32 * 64 => (32, 32),
        >= 16 * 32 => (16, 16),
        _ => (0, 0),
    };

    private static MaterialColorRow[] ParseColorTable(ref SpanReader r, int dataSetSize)
    {
        var (rows, halfsPerRow) = TableShape(dataSetSize);
        if (rows == 0)
        {
            r.Skip(dataSetSize);
            return [];
        }

        var table = new MaterialColorRow[rows];
        var start = r.Position;
        for (var i = 0; i < rows; i++)
        {
            var rowStart = start + i * halfsPerRow * 2;
            r.Position = rowStart;
            var h = new float[halfsPerRow];
            for (var j = 0; j < halfsPerRow; j++)
                h[j] = r.ReadHalf();

            var isDawntrail = rows == 32;
            table[i] = new MaterialColorRow
            {
                Diffuse = new Vector3(h[0], h[1], h[2]),
                Specular = new Vector3(h[4], h[5], h[6]),
                Emissive = new Vector3(h[8], h[9], h[10]),
                SpecularStrength = isDawntrail ? h[7] : h[3],
                Gloss = isDawntrail ? h[3] : h[7],
            };
        }

        r.Position = start + dataSetSize;
        return table;
    }

    private static string ReadCString(byte[] strings, int offset)
    {
        if (offset < 0 || offset >= strings.Length)
            return "";
        var end = Array.IndexOf(strings, (byte)0, offset);
        if (end < 0)
            end = strings.Length;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }
}

public sealed class ParsedMaterial
{
    public required string ShaderPack { get; init; }

    public required IReadOnlyList<string> TexturePaths { get; init; }

    public required IReadOnlyList<MaterialColorRow> ColorTable { get; init; }

    /// <summary>Byte offset of the color table inside the .mtrl, or -1 when the material has none.</summary>
    public int ColorTableOffset { get; init; } = -1;
}

public struct MaterialColorRow
{
    public Vector3 Diffuse;
    public Vector3 Specular;
    public Vector3 Emissive;
    public float SpecularStrength;
    public float Gloss;
}
