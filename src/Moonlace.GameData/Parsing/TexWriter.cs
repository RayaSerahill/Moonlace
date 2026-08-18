using System.Buffers.Binary;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Writes FFXIV .tex files in uncompressed B8G8R8A8 with a full mip chain,
/// and reads that same subset back (used for session textures, which Moonlace
/// always writes itself). Header layout per Lumina's MIT-licensed TexHeader:
/// 80 bytes — attributes u32, format u32, width/height/depth u16, mip count
/// u8, array size u8, lod mip indices u32×3, surface byte offsets u32×13.
/// </summary>
public static class TexWriter
{
    private const uint AttributeTextureType2D = 0x800000;
    private const uint FormatB8G8R8A8 = 0x1450;
    private const int HeaderSize = 80;

    /// <summary>Builds .tex bytes from RGBA8 pixels (top-left origin), generating mipmaps.</summary>
    public static byte[] Write(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Texture dimensions must be positive.");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException(
                $"Pixel data length {rgba.Length} does not match {width}x{height} RGBA ({width * height * 4}).");

        var mips = BuildMipChain(width, height, rgba);

        var dataSize = mips.Sum(m => m.Pixels.Length);
        var file = new byte[HeaderSize + dataSize];
        var span = file.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], AttributeTextureType2D);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], FormatB8G8R8A8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1); // depth
        span[14] = (byte)mips.Count;
        span[15] = 0; // array size
        // LodOffset: mip indices for high/med/low LODs.
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)Math.Min(1, mips.Count - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)Math.Min(2, mips.Count - 1));

        var offset = HeaderSize;
        for (var i = 0; i < 13; i++)
        {
            if (i < mips.Count)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[(28 + i * 4)..], (uint)offset);
                offset += mips[i].Pixels.Length;
            }
        }

        offset = HeaderSize;
        foreach (var (_, _, pixels) in mips)
        {
            // RGBA -> BGRA
            for (var p = 0; p < pixels.Length; p += 4)
            {
                file[offset + p] = pixels[p + 2];
                file[offset + p + 1] = pixels[p + 1];
                file[offset + p + 2] = pixels[p];
                file[offset + p + 3] = pixels[p + 3];
            }

            offset += pixels.Length;
        }

        return file;
    }

    /// <summary>
    /// Reads mip 0 of an uncompressed B8G8R8A8 .tex (the only format Moonlace
    /// writes) back to RGBA8. Returns null for any other format.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba)? TryReadB8G8R8A8(byte[] tex)
    {
        if (tex.Length < HeaderSize)
            return null;
        var span = tex.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != FormatB8G8R8A8)
            return null;

        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var surfaceOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        var size = width * height * 4;
        if (surfaceOffset <= 0 || surfaceOffset + size > tex.Length)
            return null;

        var rgba = new byte[size];
        for (var p = 0; p < size; p += 4)
        {
            rgba[p] = tex[surfaceOffset + p + 2];
            rgba[p + 1] = tex[surfaceOffset + p + 1];
            rgba[p + 2] = tex[surfaceOffset + p];
            rgba[p + 3] = tex[surfaceOffset + p + 3];
        }

        return (width, height, rgba);
    }

    private static List<(int Width, int Height, byte[] Pixels)> BuildMipChain(int width, int height, byte[] rgba)
    {
        var mips = new List<(int, int, byte[])> { (width, height, rgba) };
        var (w, h, src) = (width, height, rgba);
        while ((w > 1 || h > 1) && mips.Count < 13)
        {
            var nw = Math.Max(1, w / 2);
            var nh = Math.Max(1, h / 2);
            var dst = new byte[nw * nh * 4];
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    // 2x2 box filter (degenerates to 1x2/2x1 on rod-shaped mips).
                    var x0 = Math.Min(x * 2, w - 1);
                    var x1 = Math.Min(x * 2 + 1, w - 1);
                    var y0 = Math.Min(y * 2, h - 1);
                    var y1 = Math.Min(y * 2 + 1, h - 1);
                    for (var c = 0; c < 4; c++)
                    {
                        var sum = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                                + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                        dst[(y * nw + x) * 4 + c] = (byte)(sum / 4);
                    }
                }
            }

            mips.Add((nw, nh, dst));
            (w, h, src) = (nw, nh, dst);
        }

        return mips;
    }
}
