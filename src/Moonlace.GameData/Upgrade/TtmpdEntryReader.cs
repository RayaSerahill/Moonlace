using System.Buffers.Binary;
using System.IO.Compression;
using Lumina.Data;

namespace Moonlace.GameData.Upgrade;

/// <summary>
/// Reads one SqPack-compressed entry of a TTMPD.mpd blob. Standard (type 2)
/// and texture (type 4) entries are inflated here into a growable buffer;
/// model entries (type 3) go through Lumina's reassembly.
///
/// Lumina trusts every block header's decompressed size and inflates into a
/// buffer of exactly the file's raw size. TexTools-written textures can
/// overstate the last block (a 1×1 mip whose header claims 8 bytes while
/// its deflate stream holds the real 4), so Lumina runs past the buffer and
/// throws on the whole texture. Here a block contributes what it actually
/// inflates to, and the result is padded up to the raw size like Lumina's.
/// </summary>
internal static class TtmpdEntryReader
{
    private const uint TypeStandard = 2;
    private const uint TypeTexture = 4;
    private const uint UncompressedBlock = 32000;

    public static byte[] Read(Stream mpd, SqPackStream sqpack, long offset)
    {
        var header = ReadExact(mpd, offset, 24);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var dataStart = offset + headerSize;

        return type switch
        {
            TypeStandard => ReadStandard(mpd, offset + 24, dataStart, blockCount, rawSize),
            TypeTexture => ReadTexture(mpd, offset + 24, dataStart, blockCount, rawSize),
            _ => sqpack.ReadFile<FileResource>(offset).Data,
        };
    }

    private static byte[] ReadStandard(Stream mpd, long tableStart, long dataStart, int blockCount, uint rawSize)
    {
        // Block infos: u32 offset (from the data start), u16 compressed size, u16 decompressed size.
        var table = ReadExact(mpd, tableStart, blockCount * 8);
        var output = new MemoryStream((int)rawSize);
        for (var i = 0; i < blockCount; i++)
            ReadBlock(mpd, dataStart + BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 8)), output);
        return Finish(output, rawSize);
    }

    private static byte[] ReadTexture(Stream mpd, long tableStart, long dataStart, int lodCount, uint rawSize)
    {
        // LOD blocks (20B: compressed offset, compressed size, decompressed
        // size, first block index, block count), then one u16 compressed
        // size per block across all LODs.
        var lods = ReadExact(mpd, tableStart, lodCount * 20);
        var totalBlocks = 0;
        for (var i = 0; i < lodCount; i++)
            totalBlocks += (int)BinaryPrimitives.ReadUInt32LittleEndian(lods.AsSpan(i * 20 + 16));
        var blockSizes = ReadExact(mpd, tableStart + lodCount * 20, totalBlocks * 2);

        var output = new MemoryStream((int)rawSize);

        // The uncompressed .tex header (and mip offsets) sits before the first LOD.
        var texHeaderSize = lodCount > 0 ? BinaryPrimitives.ReadUInt32LittleEndian(lods) : 0;
        if (texHeaderSize > 0)
            output.Write(ReadExact(mpd, dataStart, (int)texHeaderSize));

        var block = 0;
        for (var i = 0; i < lodCount; i++)
        {
            var lod = lods.AsSpan(i * 20);
            long position = dataStart + BinaryPrimitives.ReadUInt32LittleEndian(lod);
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(lod[16..]);
            for (var j = 0; j < count; j++)
            {
                ReadBlock(mpd, position, output);
                position += BinaryPrimitives.ReadUInt16LittleEndian(blockSizes.AsSpan(block++ * 2));
            }
        }

        return Finish(output, rawSize);
    }

    private static byte[] Finish(MemoryStream output, uint rawSize)
    {
        if (output.Length < rawSize)
            output.SetLength(rawSize);
        return output.ToArray();
    }

    /// <summary>
    /// One data block: 16-byte header (size, 0, compressed size or 32000,
    /// decompressed size), then the payload. A compressed block may inflate
    /// to less than its header claims; only the real bytes are kept.
    /// </summary>
    private static void ReadBlock(Stream mpd, long position, MemoryStream output)
    {
        var header = ReadExact(mpd, position, 16);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));

        if (compressedSize == UncompressedBlock)
        {
            output.Write(ReadExact(mpd, position + size, dataSize));
            return;
        }

        var compressed = ReadExact(mpd, position + size, (int)compressedSize);
        using var inflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var data = new byte[dataSize];
        var read = inflate.ReadAtLeast(data, dataSize, throwOnEndOfStream: false);
        output.Write(data, 0, read);
    }

    private static byte[] ReadExact(Stream stream, long position, int count)
    {
        var buffer = new byte[count];
        stream.Position = position;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
