using System.Buffers.Binary;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Rewrites the editable parts of a .mtrl by patching a copy of the original
/// bytes in place. Only the color table is written this way; everything else
/// in the file (strings, samplers, shader constants) stays byte-identical,
/// which keeps the write path safe: fields Moonlace cannot round-trip are
/// never touched.
/// </summary>
public static class MtrlWriter
{
    /// <summary>
    /// Returns a copy of <paramref name="original"/> with its color table
    /// replaced by <paramref name="rows"/>. Scalar slots follow the same
    /// version-dependent layout the parser reads (see MtrlParser).
    /// </summary>
    public static byte[] PatchColorTable(byte[] original, IReadOnlyList<MaterialColorRow> rows)
    {
        var parsed = MtrlParser.Parse(original);
        if (parsed.ColorTableOffset < 0)
            throw new InvalidOperationException("This material has no color table to edit.");
        if (rows.Count != parsed.ColorTable.Count)
            throw new ArgumentException(
                $"Row count mismatch: material has {parsed.ColorTable.Count} rows, {rows.Count} given.");

        var dataSetSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(4)) >> 16);
        var (tableRows, halfsPerRow) = MtrlParser.TableShape(dataSetSize);
        var isDawntrail = tableRows == 32;

        var result = (byte[])original.Clone();
        for (var i = 0; i < rows.Count; i++)
        {
            var rowOffset = parsed.ColorTableOffset + i * halfsPerRow * 2;
            var row = rows[i];

            WriteHalf(result, rowOffset + 0, row.Diffuse.X);
            WriteHalf(result, rowOffset + 2, row.Diffuse.Y);
            WriteHalf(result, rowOffset + 4, row.Diffuse.Z);
            WriteHalf(result, rowOffset + 8, row.Specular.X);
            WriteHalf(result, rowOffset + 10, row.Specular.Y);
            WriteHalf(result, rowOffset + 12, row.Specular.Z);
            WriteHalf(result, rowOffset + 16, row.Emissive.X);
            WriteHalf(result, rowOffset + 18, row.Emissive.Y);
            WriteHalf(result, rowOffset + 20, row.Emissive.Z);
            WriteHalf(result, rowOffset + 6, isDawntrail ? row.Gloss : row.SpecularStrength);
            WriteHalf(result, rowOffset + 14, isDawntrail ? row.SpecularStrength : row.Gloss);
        }

        return result;
    }

    private static void WriteHalf(byte[] buffer, int offset, float value) =>
        BinaryPrimitives.WriteHalfLittleEndian(buffer.AsSpan(offset), (Half)value);

    /// <summary>
    /// Returns a rebuilt copy of <paramref name="original"/> whose texture
    /// slots point at <paramref name="newPaths"/> (one per existing slot, in
    /// order). Texture paths live in the .mtrl string table, so this rebuilds
    /// the table and fixes every offset that references it (texture entries,
    /// UV/color-set names, shader package name); everything after the string
    /// table — additional data, color tables, shader keys/constants/samplers —
    /// is preserved byte-identical. Samplers reference textures by slot
    /// index, so they stay valid.
    /// </summary>
    public static byte[] ReplaceTexturePaths(byte[] original, IReadOnlyList<string> newPaths)
    {
        var span = original.AsSpan();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var fileAndDataSetSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var dataSetSize = (ushort)(fileAndDataSetSize >> 16);
        int stringTableSize = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        int shaderNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        int textureCount = span[12];
        int uvSetCount = span[13];
        int colorSetCount = span[14];
        int additionalDataSize = span[15];

        if (newPaths.Count != textureCount)
            throw new ArgumentException($"Material has {textureCount} texture slots, {newPaths.Count} paths given.");
        foreach (var path in newPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".tex", StringComparison.Ordinal))
                throw new ArgumentException($"\"{path}\" is not a .tex game path.");
        }

        var pos = 16;
        var textureFlags = new ushort[textureCount];
        var oldTextureOffsets = new int[textureCount];
        for (var i = 0; i < textureCount; i++)
        {
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            oldTextureOffsets[i] = (ushort)entry;
            textureFlags[i] = (ushort)(entry >> 16);
            pos += 4;
        }

        var uvSets = new (int NameOffset, byte Index, byte Unknown)[uvSetCount];
        for (var i = 0; i < uvSetCount; i++)
        {
            uvSets[i] = (BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]), span[pos + 2], span[pos + 3]);
            pos += 4;
        }

        var colorSets = new (int NameOffset, byte Index, byte Unknown)[colorSetCount];
        for (var i = 0; i < colorSetCount; i++)
        {
            colorSets[i] = (BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]), span[pos + 2], span[pos + 3]);
            pos += 4;
        }

        var oldStrings = original.AsSpan(pos, stringTableSize).ToArray();
        var tail = original.AsSpan(pos + stringTableSize).ToArray(); // additional data onward, unchanged

        string OldString(int offset)
        {
            if (offset < 0 || offset >= oldStrings.Length)
                return "";
            var end = Array.IndexOf(oldStrings, (byte)0, offset);
            if (end < 0)
                end = oldStrings.Length;
            return System.Text.Encoding.UTF8.GetString(oldStrings, offset, end - offset);
        }

        // Rebuild the string table with new offsets, deduplicating identical strings.
        var table = new MemoryStream();
        var offsetOf = new Dictionary<string, ushort>(StringComparer.Ordinal);
        ushort Put(string value)
        {
            if (offsetOf.TryGetValue(value, out var existing))
                return existing;
            var offset = checked((ushort)table.Position);
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            table.Write(bytes);
            table.WriteByte(0);
            offsetOf[value] = offset;
            return offset;
        }

        var newTextureOffsets = newPaths.Select(Put).ToArray();
        var newUvSetOffsets = uvSets.Select(s => Put(OldString(s.NameOffset))).ToArray();
        var newColorSetOffsets = colorSets.Select(s => Put(OldString(s.NameOffset))).ToArray();
        var newShaderOffset = Put(OldString(shaderNameOffset));
        while (table.Position % 4 != 0)
            table.WriteByte(0);
        var newStrings = table.ToArray();

        // Reassemble.
        var result = new MemoryStream();
        void WriteU32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            result.Write(b);
        }

        void WriteU16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            result.Write(b);
        }

        var newFileSize = 16 + textureCount * 4 + uvSetCount * 4 + colorSetCount * 4 + newStrings.Length + tail.Length;
        WriteU32(version);
        WriteU32((uint)((ushort)newFileSize | (dataSetSize << 16)));
        WriteU16(checked((ushort)newStrings.Length));
        WriteU16(newShaderOffset);
        result.WriteByte((byte)textureCount);
        result.WriteByte((byte)uvSetCount);
        result.WriteByte((byte)colorSetCount);
        result.WriteByte((byte)additionalDataSize);

        for (var i = 0; i < textureCount; i++)
            WriteU32((uint)(newTextureOffsets[i] | (textureFlags[i] << 16)));
        for (var i = 0; i < uvSetCount; i++)
        {
            WriteU16(newUvSetOffsets[i]);
            result.WriteByte(uvSets[i].Index);
            result.WriteByte(uvSets[i].Unknown);
        }

        for (var i = 0; i < colorSetCount; i++)
        {
            WriteU16(newColorSetOffsets[i]);
            result.WriteByte(colorSets[i].Index);
            result.WriteByte(colorSets[i].Unknown);
        }

        result.Write(newStrings);
        result.Write(tail);
        return result.ToArray();
    }
}
