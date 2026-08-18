using System.Buffers.Binary;
using System.Numerics;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Writes an FFXIV .mdl file from a parsed template plus (possibly replaced)
/// geometry. Emits format version 5, which current game clients still load
/// and which Lumina's own reader can parse — giving an independent
/// verification path for round-trip tests.
///
/// The writer reuses the template's string table, name-offset arrays and
/// element IDs verbatim (so all string offsets stay valid), rebuilds vertex
/// declarations/meshes/submeshes/LODs/bounds from the new geometry, converts
/// bone tables to the v5 encoding, and drops shape (morph) data.
/// </summary>
public static class MdlWriter
{
    private const int Stream0Stride = 20; // position 12 + blend weights 4 + blend indices 4
    private const int Stream1Stride = 36; // normal 12 + tangent 4 + color 4 + uv 16

    public static byte[] Write(ParsedModel template, IReadOnlyList<ParsedMesh> meshes, IReadOnlyList<ushort[]> boneTables)
    {
        var edit = template.EditData
            ?? throw new InvalidOperationException("Template model was parsed without edit data.");

        if (meshes.Count == 0)
            throw new ArgumentException("Cannot write a model with no meshes.");
        if (meshes.Any(m => m.Vertices.Length > ushort.MaxValue))
            throw new ArgumentException("A mesh exceeds 65535 vertices, which the MDL format cannot store.");
        if (boneTables.Any(t => t.Length > 64))
            throw new ArgumentException("A bone table exceeds 64 bones, which MDL v5 cannot store.");

        // --- Geometry buffers ---
        var vertexData = new MemoryStream();
        var indexData = new MemoryStream();
        var meshRecords = new List<MeshRecord>();
        foreach (var mesh in meshes)
        {
            var rec = new MeshRecord
            {
                Mesh = mesh,
                Stream0Offset = (uint)vertexData.Position,
            };
            WriteStream0(vertexData, mesh.Vertices);
            rec.Stream1Offset = (uint)vertexData.Position;
            WriteStream1(vertexData, mesh.Vertices);

            rec.StartIndex = (uint)(indexData.Position / 2);
            foreach (var index in mesh.Indices)
                WriteU16(indexData, (ushort)index);
            // Index data is 16-byte aligned between meshes in official files.
            while (indexData.Position % 16 != 0)
                indexData.WriteByte(0);

            meshRecords.Add(rec);
        }

        var vertexBytes = vertexData.ToArray();
        var indexBytes = indexData.ToArray();

        // --- Bounds ---
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
        {
            foreach (ref readonly var v in mesh.Vertices.AsSpan())
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
        }

        var radius = MathF.Max(min.Length(), max.Length());

        // --- Runtime section ---
        var runtime = new MemoryStream();
        WriteU16(runtime, edit.StringCount);
        WriteU16(runtime, 0);
        WriteU32(runtime, (uint)edit.StringsRaw.Length);
        runtime.Write(edit.StringsRaw);

        WriteModelHeader(runtime, edit, radius, meshes.Count, boneTables.Count);
        runtime.Write(edit.ElementIdsRaw);

        var totalIndices = meshes.Sum(m => m.Indices.Length);
        for (var lod = 0; lod < 3; lod++)
            WriteLod(runtime, edit, meshes.Count, totalIndices, (uint)vertexBytes.Length, (uint)indexBytes.Length);

        foreach (var rec in meshRecords)
            WriteMesh(runtime, rec, meshRecords.IndexOf(rec));

        foreach (var offset in edit.AttributeNameOffsets)
            WriteU32(runtime, offset);

        // Submeshes: one per mesh, all attributes visible, no bone map slice.
        foreach (var rec in meshRecords)
        {
            WriteU32(runtime, rec.StartIndex);
            WriteU32(runtime, (uint)rec.Mesh.Indices.Length);
            WriteU32(runtime, 0); // attribute mask
            WriteU16(runtime, 0); // bone start index
            WriteU16(runtime, 0); // bone count
        }

        foreach (var offset in edit.MaterialNameOffsets)
            WriteU32(runtime, offset);
        foreach (var offset in edit.BoneNameOffsets)
            WriteU32(runtime, offset);

        // Bone tables, v5 encoding: 64 ushorts + u32 count.
        foreach (var table in boneTables)
        {
            for (var i = 0; i < 64; i++)
                WriteU16(runtime, i < table.Length ? table[i] : (ushort)0);
            WriteU32(runtime, (uint)table.Length);
        }

        WriteU32(runtime, 0); // submesh bone map byte size
        runtime.WriteByte(0); // padding amount

        for (var box = 0; box < 4; box++)
            WriteBoundingBox(runtime, min, max);
        for (var bone = 0; bone < edit.BoneNameOffsets.Length; bone++)
            WriteBoundingBox(runtime, min, max);

        var runtimeBytes = runtime.ToArray();

        // --- Assemble ---
        var stackSize = meshes.Count * 17 * 8;
        var vertexStart = 68 + stackSize + runtimeBytes.Length;
        var indexStart = vertexStart + vertexBytes.Length;

        var file = new MemoryStream();
        WriteU32(file, MdlParser.VersionV5);
        WriteU32(file, (uint)stackSize);
        WriteU32(file, (uint)runtimeBytes.Length);
        WriteU16(file, (ushort)meshes.Count); // vertex declaration count
        WriteU16(file, (ushort)edit.MaterialNameOffsets.Length);
        for (var lod = 0; lod < 3; lod++)
            WriteU32(file, (uint)vertexStart);
        for (var lod = 0; lod < 3; lod++)
            WriteU32(file, (uint)indexStart);
        for (var lod = 0; lod < 3; lod++)
            WriteU32(file, (uint)vertexBytes.Length);
        for (var lod = 0; lod < 3; lod++)
            WriteU32(file, (uint)indexBytes.Length);
        file.WriteByte(3); // lod count
        file.WriteByte(0); // index buffer streaming
        file.WriteByte(0); // edge geometry
        file.WriteByte(0);

        foreach (var _ in meshes)
            WriteVertexDeclaration(file);

        file.Write(runtimeBytes);
        file.Write(vertexBytes);
        file.Write(indexBytes);
        return file.ToArray();
    }

    private sealed class MeshRecord
    {
        public required ParsedMesh Mesh { get; init; }

        public uint Stream0Offset { get; init; }

        public uint Stream1Offset { get; set; }

        public uint StartIndex { get; set; }
    }

    private static void WriteModelHeader(MemoryStream s, MdlEditData edit, float radius, int meshCount, int boneTableCount)
    {
        WriteF32(s, radius);
        WriteU16(s, (ushort)meshCount);
        WriteU16(s, (ushort)edit.AttributeNameOffsets.Length);
        WriteU16(s, (ushort)meshCount); // submesh count (one per mesh)
        WriteU16(s, (ushort)edit.MaterialNameOffsets.Length);
        WriteU16(s, (ushort)edit.BoneNameOffsets.Length);
        WriteU16(s, (ushort)boneTableCount);
        WriteU16(s, 0); // shapes
        WriteU16(s, 0);
        WriteU16(s, 0);
        s.WriteByte(3); // lod count
        s.WriteByte(0); // flags1
        WriteU16(s, (ushort)(edit.ElementIdsRaw.Length / 32));
        s.WriteByte(0); // terrain shadow mesh count
        s.WriteByte(0); // flags2
        WriteF32(s, 0); // model clip-out distance
        WriteF32(s, 0); // shadow clip-out distance
        WriteU16(s, 0);
        WriteU16(s, 0); // terrain shadow submesh count
        WriteU32(s, 0); // flags3 + bg material indices
        WriteU16(s, 0); // bone table array count total (v6 only)
        // Remaining reserved fields; the header is 56 bytes on disk.
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0);
        WriteU16(s, 0);
    }

    private static void WriteLod(MemoryStream s, MdlEditData edit, int meshCount, int totalIndices, uint vertexSize, uint indexSize)
    {
        WriteU16(s, 0); // mesh index
        WriteU16(s, (ushort)meshCount);
        WriteF32(s, edit.Lod0ModelRange);
        WriteF32(s, edit.Lod0TextureRange);
        for (var i = 0; i < 8; i++)
            WriteU16(s, 0); // water/shadow/terrain-shadow/vertical-fog ranges
        WriteU32(s, 0); // edge geometry size
        WriteU32(s, 0); // edge geometry data offset
        WriteU32(s, (uint)(totalIndices / 3)); // polygon count
        WriteU32(s, 0);
        WriteU32(s, vertexSize);
        WriteU32(s, indexSize);
        WriteU32(s, 0); // vertex data offset within lod block (buffers shared, lod-relative)
        WriteU32(s, 0); // index data offset
    }

    private static void WriteMesh(MemoryStream s, MeshRecord rec, int meshIndex)
    {
        WriteU16(s, (ushort)rec.Mesh.Vertices.Length);
        WriteU16(s, 0);
        WriteU32(s, (uint)rec.Mesh.Indices.Length);
        WriteU16(s, (ushort)rec.Mesh.MaterialIndex);
        WriteU16(s, (ushort)meshIndex); // submesh index (1:1)
        WriteU16(s, 1); // submesh count
        WriteU16(s, (ushort)rec.Mesh.BoneTableIndex);
        WriteU32(s, rec.StartIndex);
        WriteU32(s, rec.Stream0Offset);
        WriteU32(s, rec.Stream1Offset);
        WriteU32(s, 0);
        s.WriteByte(Stream0Stride);
        s.WriteByte(Stream1Stride);
        s.WriteByte(0);
        s.WriteByte(2); // vertex stream count
    }

    private static void WriteVertexDeclaration(MemoryStream s)
    {
        // (stream, offset, type, usage): types — 2 Single3, 3 Single4, 5 UInt, 8 ByteFloat4.
        Span<(byte Stream, byte Offset, byte Type, byte Usage)> elements =
        [
            (0, 0, 2, 0),   // position
            (0, 12, 8, 1),  // blend weights
            (0, 16, 5, 2),  // blend indices
            (1, 0, 2, 3),   // normal
            (1, 12, 8, 6),  // tangent1
            (1, 16, 8, 7),  // color
            (1, 20, 3, 4),  // uv (two channels in xyzw)
        ];

        foreach (var (stream, offset, type, usage) in elements)
        {
            s.WriteByte(stream);
            s.WriteByte(offset);
            s.WriteByte(type);
            s.WriteByte(usage);
            WriteU32(s, 0); // usage index + padding
        }

        s.WriteByte(255); // terminator
        for (var i = 0; i < 7; i++)
            s.WriteByte(0);
        for (var slot = elements.Length + 1; slot < 17; slot++)
            WriteU64(s, 0);
    }

    private static void WriteStream0(MemoryStream s, ParsedVertex[] vertices)
    {
        foreach (ref readonly var v in vertices.AsSpan())
        {
            WriteF32(s, v.Position.X);
            WriteF32(s, v.Position.Y);
            WriteF32(s, v.Position.Z);
            WriteNormalizedWeights(s, v.BlendWeights);
            WriteU32(s, v.BlendIndicesPacked);
        }
    }

    private static void WriteStream1(MemoryStream s, ParsedVertex[] vertices)
    {
        foreach (ref readonly var v in vertices.AsSpan())
        {
            WriteF32(s, v.Normal.X);
            WriteF32(s, v.Normal.Y);
            WriteF32(s, v.Normal.Z);
            // Tangent −1..1 → 0..255, handedness in W.
            s.WriteByte(ToByteFloat((v.Tangent.X + 1f) * 0.5f));
            s.WriteByte(ToByteFloat((v.Tangent.Y + 1f) * 0.5f));
            s.WriteByte(ToByteFloat((v.Tangent.Z + 1f) * 0.5f));
            s.WriteByte(ToByteFloat((v.Tangent.W + 1f) * 0.5f));
            s.WriteByte(ToByteFloat(v.Color.X));
            s.WriteByte(ToByteFloat(v.Color.Y));
            s.WriteByte(ToByteFloat(v.Color.Z));
            s.WriteByte(ToByteFloat(v.Color.W));
            WriteF32(s, v.Uv.X);
            WriteF32(s, v.Uv.Y);
            WriteF32(s, 0);
            WriteF32(s, 0);
        }
    }

    /// <summary>Weights must sum to exactly 255 in byte form or the game deforms wrongly.</summary>
    private static void WriteNormalizedWeights(MemoryStream s, Vector4 weights)
    {
        var sum = weights.X + weights.Y + weights.Z + weights.W;
        if (sum <= 0)
        {
            s.WriteByte(255);
            s.WriteByte(0);
            s.WriteByte(0);
            s.WriteByte(0);
            return;
        }

        Span<byte> bytes =
        [
            (byte)Math.Clamp(MathF.Round(weights.X / sum * 255f), 0, 255),
            (byte)Math.Clamp(MathF.Round(weights.Y / sum * 255f), 0, 255),
            (byte)Math.Clamp(MathF.Round(weights.Z / sum * 255f), 0, 255),
            (byte)Math.Clamp(MathF.Round(weights.W / sum * 255f), 0, 255),
        ];

        // Push rounding error into the largest weight.
        var total = bytes[0] + bytes[1] + bytes[2] + bytes[3];
        if (total != 255)
        {
            var largest = 0;
            for (var i = 1; i < 4; i++)
            {
                if (bytes[i] > bytes[largest])
                    largest = i;
            }

            bytes[largest] = (byte)Math.Clamp(bytes[largest] + (255 - total), 0, 255);
        }

        s.Write(bytes);
    }

    private static byte ToByteFloat(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0, 255);

    private static void WriteBoundingBox(MemoryStream s, Vector3 min, Vector3 max)
    {
        WriteF32(s, min.X);
        WriteF32(s, min.Y);
        WriteF32(s, min.Z);
        WriteF32(s, 1);
        WriteF32(s, max.X);
        WriteF32(s, max.Y);
        WriteF32(s, max.Z);
        WriteF32(s, 1);
    }

    private static void WriteU16(MemoryStream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteU32(MemoryStream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteU64(MemoryStream s, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteF32(MemoryStream s, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        s.Write(b);
    }
}
