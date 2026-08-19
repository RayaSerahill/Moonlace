using System.Buffers.Binary;
using System.Text;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Renames material names inside a .mdl file by patching the string table in
/// place. Every replacement must have the same UTF-8 byte length as the
/// original (e.g. a race code swap, "c0101" → "c0801"), so all string offsets
/// stay valid and the rest of the file — including shape data the
/// <see cref="MdlWriter"/> round-trip would drop — is preserved byte-exact.
/// </summary>
public static class MdlMaterialRenamer
{
    /// <summary>
    /// Returns a copy of the model with each material name replaced by
    /// <paramref name="rename"/>'s result (null keeps a name unchanged).
    /// </summary>
    public static byte[] RenameMaterials(byte[] mdl, Func<string, string?> rename)
    {
        // The parser locates the material name offsets; the header gives us
        // where the string blob sits in the raw file: 68-byte file header,
        // stack (vertex declarations), then u16 string count + u16 pad +
        // u32 string size + the blob itself.
        var parsed = MdlParser.Parse(mdl);
        var edit = parsed.EditData
            ?? throw new InvalidOperationException("Model was parsed without edit data.");

        var stackSize = BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(4));
        var stringsStart = 68 + (int)stackSize + 8;
        var stringsSize = BinaryPrimitives.ReadUInt32LittleEndian(mdl.AsSpan(68 + (int)stackSize + 4));

        var patched = (byte[])mdl.Clone();
        foreach (var offset in edit.MaterialNameOffsets.Distinct())
        {
            var name = ReadCString(mdl, stringsStart + (int)offset, stringsStart + (int)stringsSize);
            var renamed = rename(name);
            if (renamed is null || renamed == name)
                continue;

            var oldBytes = Encoding.UTF8.GetBytes(name);
            var newBytes = Encoding.UTF8.GetBytes(renamed);
            if (newBytes.Length != oldBytes.Length)
                throw new ArgumentException(
                    $"Material rename must keep the byte length: \"{name}\" ({oldBytes.Length}) → \"{renamed}\" ({newBytes.Length}).");

            newBytes.CopyTo(patched, stringsStart + (int)offset);
        }

        // The patch must parse back cleanly with exactly the renamed names.
        var check = MdlParser.Parse(patched);
        var expected = parsed.MaterialNames.Select(n => rename(n) ?? n);
        if (!check.MaterialNames.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Internal error: the renamed model failed verification.");

        return patched;
    }

    private static string ReadCString(byte[] data, int offset, int limit)
    {
        var end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0 || end > limit)
            end = limit;
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }
}
