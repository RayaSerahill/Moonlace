using System.Buffers.Binary;
using System.Text;

namespace Moonlace.GameData.Parsing;

public sealed class PapParseException(string message) : Exception(message);

/// <summary>
/// Minimal reader/patcher for .pap animation containers. Only the header and
/// the fixed-size animation info entries are touched; the havok payload is
/// never modified. Layout (verified against Dawntrail files):
///
///   0x00  char[4] "pap "  magic
///   0x04  u32  version (0x00020001)
///   0x08  u16  animation count
///   0x0a  u16  model id (informational; mods ship mismatching ones freely)
///   0x0c  u16  model type
///   0x0e  u32  info offset → count × 40-byte entries
///   0x12  u32  havok offset
///   0x16  u32  footer (timeline) offset
///
/// Each info entry is a NUL-padded char[32] name followed by 8 bytes of
/// index/flag data. The name is what a timeline's animation track (C010)
/// references to pick the clip, so retargeting an animation onto another
/// emote patches exactly these fixed-width name fields — offsets never move.
/// </summary>
public static class PapAnimations
{
    private const int HeaderSize = 0x1a;
    private const int EntrySize = 40;
    private const int NameSize = 32;

    public static bool LooksLikePap(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[0] == 'p' && data[1] == 'a' && data[2] == 'p' && data[3] == ' ';

    /// <summary>The names of the animations the container holds, in entry order.</summary>
    public static IReadOnlyList<string> ReadNames(byte[] data)
    {
        var (count, infoOffset) = ReadHeader(data);
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = ReadName(data, infoOffset + i * EntrySize);
        return names;
    }

    /// <summary>
    /// Returns a copy with each animation name replaced by
    /// <paramref name="rename"/>'s answer (null keeps the name). Returns the
    /// input array unchanged when nothing renames.
    /// </summary>
    public static byte[] RenameAnimations(byte[] data, Func<string, string?> rename)
    {
        var (count, infoOffset) = ReadHeader(data);
        byte[]? patched = null;
        for (var i = 0; i < count; i++)
        {
            var offset = infoOffset + i * EntrySize;
            var renamed = rename(ReadName(data, offset));
            if (renamed is null)
                continue;
            if (Encoding.ASCII.GetByteCount(renamed) >= NameSize)
                throw new PapParseException($"Animation name \"{renamed}\" does not fit the 31-character pap name field.");

            patched ??= (byte[])data.Clone();
            Array.Clear(patched, offset, NameSize);
            Encoding.ASCII.GetBytes(renamed, patched.AsSpan(offset, NameSize - 1));
        }

        return patched ?? data;
    }

    private static (int Count, int InfoOffset) ReadHeader(byte[] data)
    {
        if (!LooksLikePap(data))
            throw new PapParseException("Not a .pap animation container (bad magic).");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x08));
        var infoOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0e)));
        if (infoOffset < HeaderSize || infoOffset + (long)count * EntrySize > data.Length)
            throw new PapParseException("Truncated .pap: the animation info entries fall outside the file.");
        return (count, infoOffset);
    }

    private static string ReadName(byte[] data, int offset)
    {
        var field = data.AsSpan(offset, NameSize);
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(field[..(end < 0 ? NameSize : end)]);
    }
}
