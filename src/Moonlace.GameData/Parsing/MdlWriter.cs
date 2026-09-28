using System.Buffers.Binary;
using System.Numerics;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Writes an FFXIV .mdl file from a parsed template plus (possibly replaced)
/// geometry. Emits format version 5, which current game clients still load
/// and which Lumina's own reader can parse — giving an independent
/// verification path for round-trip tests.
///
/// The writer reuses the template's string table, name-offset arrays,
/// element IDs and submesh bone map verbatim (so all string offsets stay
/// valid), rebuilds vertex declarations/meshes/LODs/bounds from the new
/// geometry, preserves each mesh's submesh partition and attribute masks
/// (falling back to one covering submesh when the partition no longer fits
/// the geometry), converts bone tables to the v5 encoding, and drops shape
/// (morph) data. Bones the template does not know (an imported model rigged
/// to a body kit's own skeleton) are appended to the bone list, and
/// materials it does not know (a third-party mod's material typed into the
/// Model tab) to the material list, with their names added after the
/// existing strings so every old offset still holds.
/// </summary>
public static class MdlWriter
{
    private const int Stream0Stride = 20; // position 12 + blend weights 4 + blend indices 4
    private const int Stream1Stride = 36; // normal 12 + tangent 4 + color 4 + uv 16

    /// <param name="boneNames">
    /// The full bone list the bone tables index into. It must start with the
    /// template's own bones; anything after them is appended as new bones.
    /// Null keeps the template's list.
    /// </param>
    /// <param name="materialNames">
    /// The full material list mesh material indices point into, in any order
    /// and length (materials may be added or removed). Names the template
    /// already has reuse their string; new ones are appended. Null keeps the
    /// template's list.
    /// </param>
    public static byte[] Write(
        ParsedModel template, IReadOnlyList<ParsedMesh> meshes, IReadOnlyList<ushort[]> boneTables,
        IReadOnlyList<string>? boneNames = null, IReadOnlyList<string>? materialNames = null)
    {
        var edit = template.EditData
            ?? throw new InvalidOperationException("Template model was parsed without edit data.");
        var (strings, stringCount, materialNameOffsets, boneNameOffsets) = AppendNames(
            edit, template.MaterialNames, materialNames, template.BoneNames, boneNames);
        if (meshes.Any(m => m.MaterialIndex < 0 || m.MaterialIndex >= materialNameOffsets.Length))
            throw new ArgumentException("A mesh points at a material slot the model does not have.");

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
        var submeshRecords = new List<ParsedSubmesh>();
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

            // Preserve the mesh's submesh partition (attribute masks, bone
            // map slices) when it still fits the geometry; otherwise emit one
            // covering submesh with no attributes, as for imported geometry.
            rec.SubmeshIndex = submeshRecords.Count;
            var partition = mesh.Submeshes;
            if (partition.Count == 0 || partition.Any(s =>
                    s.IndexOffset > mesh.Indices.Length || s.IndexOffset + s.IndexCount > mesh.Indices.Length))
            {
                partition = [new ParsedSubmesh(0, (uint)mesh.Indices.Length, 0, 0, 0)];
            }

            foreach (var sub in partition)
                submeshRecords.Add(sub with { IndexOffset = rec.StartIndex + sub.IndexOffset });
            rec.SubmeshCount = submeshRecords.Count - rec.SubmeshIndex;

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
        WriteU16(runtime, stringCount);
        WriteU16(runtime, 0);
        WriteU32(runtime, (uint)strings.Length);
        runtime.Write(strings);

        WriteModelHeader(
            runtime, edit, radius, meshes.Count, submeshRecords.Count,
            materialNameOffsets.Length, boneNameOffsets.Length, boneTables.Count);
        runtime.Write(edit.ElementIdsRaw);

        var totalIndices = meshes.Sum(m => m.Indices.Length);
        for (var lod = 0; lod < 3; lod++)
            WriteLod(runtime, edit, meshes.Count, totalIndices, (uint)vertexBytes.Length, (uint)indexBytes.Length);

        foreach (var rec in meshRecords)
            WriteMesh(runtime, rec);

        foreach (var offset in edit.AttributeNameOffsets)
            WriteU32(runtime, offset);

        foreach (var sub in submeshRecords)
        {
            WriteU32(runtime, sub.IndexOffset);
            WriteU32(runtime, sub.IndexCount);
            WriteU32(runtime, sub.AttributeMask);
            WriteU16(runtime, sub.BoneStartIndex);
            WriteU16(runtime, sub.BoneCount);
        }

        foreach (var offset in materialNameOffsets)
            WriteU32(runtime, offset);
        foreach (var offset in boneNameOffsets)
            WriteU32(runtime, offset);

        // Bone tables, v5 encoding: 64 ushorts + u32 count.
        foreach (var table in boneTables)
        {
            for (var i = 0; i < 64; i++)
                WriteU16(runtime, i < table.Length ? table[i] : (ushort)0);
            WriteU32(runtime, (uint)table.Length);
        }

        WriteU32(runtime, (uint)edit.SubmeshBoneMapRaw.Length);
        runtime.Write(edit.SubmeshBoneMapRaw);
        runtime.WriteByte(0); // padding amount

        for (var box = 0; box < 4; box++)
            WriteBoundingBox(runtime, min, max);
        for (var bone = 0; bone < boneNameOffsets.Length; bone++)
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
        WriteU16(file, (ushort)materialNameOffsets.Length);
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

    /// <summary>
    /// The string table plus material- and bone-name offsets: the material
    /// list may be any list (names the template has reuse their strings), the
    /// bone list must start with the template's bones. New names are appended. New names go right after
    /// the existing strings (kept byte for byte, minus trailing padding),
    /// padded back to 4-byte alignment.
    /// </summary>
    private static (byte[] Strings, ushort StringCount, uint[] MaterialNameOffsets, uint[] BoneNameOffsets) AppendNames(
        MdlEditData edit,
        IReadOnlyList<string> templateMaterials, IReadOnlyList<string>? materialNames,
        IReadOnlyList<string> templateBones, IReadOnlyList<string>? boneNames)
    {
        // Materials: a template name reuses its offset, anything else is new.
        var existingMaterialOffset = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var i = 0; i < templateMaterials.Count && i < edit.MaterialNameOffsets.Length; i++)
            existingMaterialOffset.TryAdd(templateMaterials[i], edit.MaterialNameOffsets[i]);
        var materialList = materialNames ?? templateMaterials;
        if (materialList.Count > ushort.MaxValue)
            throw new ArgumentException("Too many materials for the MDL format.", nameof(materialNames));
        var newMaterials = materialList.Where(n => !existingMaterialOffset.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToArray();

        var newBones = ExtraNames(templateBones, boneNames, "bone", nameof(boneNames));
        if (newMaterials.Length == 0 && newBones.Count == 0)
            return (edit.StringsRaw, edit.StringCount, materialList.Select(n => existingMaterialOffset[n]).ToArray(), edit.BoneNameOffsets);
        if (edit.StringCount + newMaterials.Length + newBones.Count > ushort.MaxValue
            || templateBones.Count + newBones.Count > ushort.MaxValue)
            throw new ArgumentException("Too many names for the MDL format.");

        // Drop the old alignment padding (keeping the last terminator) so the
        // new names follow directly; readers that walk the table string by
        // string (Lumina) would otherwise stop at the padding.
        var kept = edit.StringsRaw.Length;
        while (kept > 1 && edit.StringsRaw[kept - 1] == 0 && edit.StringsRaw[kept - 2] == 0)
            kept--;
        var lastReferenced = edit.AttributeNameOffsets
            .Concat(edit.MaterialNameOffsets).Concat(edit.BoneNameOffsets)
            .DefaultIfEmpty(0u).Max();
        kept = Math.Max(kept, (int)Math.Min(lastReferenced + 1, (uint)edit.StringsRaw.Length));

        var strings = new MemoryStream();
        strings.Write(edit.StringsRaw, 0, kept);
        var boneOffsets = new List<uint>(edit.BoneNameOffsets);
        foreach (var name in newMaterials)
            existingMaterialOffset[name] = AppendString(strings, name);
        var materialOffsets = materialList.Select(n => existingMaterialOffset[n]).ToList();
        foreach (var name in newBones)
            boneOffsets.Add(AppendString(strings, name));

        while (strings.Position % 4 != 0)
            strings.WriteByte(0);

        var added = newMaterials.Length + newBones.Count;
        return (strings.ToArray(), (ushort)(edit.StringCount + added), [.. materialOffsets], [.. boneOffsets]);
    }

    /// <summary>The names past the template's own list; the list must start with the template's names.</summary>
    private static IReadOnlyList<string> ExtraNames(
        IReadOnlyList<string> template, IReadOnlyList<string>? full, string kind, string parameter)
    {
        if (full is null || full.Count == template.Count)
            return [];
        if (full.Count < template.Count || !full.Take(template.Count).SequenceEqual(template))
            throw new ArgumentException($"The {kind} list must start with the template's own {kind}s.", parameter);
        return full.Skip(template.Count).ToArray();
    }

    private static uint AppendString(MemoryStream strings, string value)
    {
        var offset = (uint)strings.Position;
        strings.Write(System.Text.Encoding.UTF8.GetBytes(value));
        strings.WriteByte(0);
        return offset;
    }

    private sealed class MeshRecord
    {
        public required ParsedMesh Mesh { get; init; }

        public uint Stream0Offset { get; init; }

        public uint Stream1Offset { get; set; }

        public uint StartIndex { get; set; }

        public int SubmeshIndex { get; set; }

        public int SubmeshCount { get; set; }
    }

    private static void WriteModelHeader(
        MemoryStream s, MdlEditData edit, float radius, int meshCount, int submeshCount,
        int materialCount, int boneCount, int boneTableCount)
    {
        WriteF32(s, radius);
        WriteU16(s, (ushort)meshCount);
        WriteU16(s, (ushort)edit.AttributeNameOffsets.Length);
        WriteU16(s, (ushort)submeshCount);
        WriteU16(s, (ushort)materialCount);
        WriteU16(s, (ushort)boneCount);
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

    private static void WriteMesh(MemoryStream s, MeshRecord rec)
    {
        WriteU16(s, (ushort)rec.Mesh.Vertices.Length);
        WriteU16(s, 0);
        WriteU32(s, (uint)rec.Mesh.Indices.Length);
        WriteU16(s, (ushort)rec.Mesh.MaterialIndex);
        WriteU16(s, (ushort)rec.SubmeshIndex);
        WriteU16(s, (ushort)rec.SubmeshCount);
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
