using System.Numerics;
using System.Text;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Moonlace-owned parser for FFXIV .mdl files (versions 5 and 6).
///
/// Struct layouts follow Lumina's MIT-licensed MdlStructs documentation of the
/// format. Lumina's own MdlFile cannot read version 6 (Dawntrail) models —
/// v6 changed the bone table encoding — which is why this parser exists.
/// Everything v1 rendering needs (vertex declarations, strings, mesh/submesh
/// tables, material names, LODs and the vertex/index buffers) is stored
/// *before* the bone tables, so this parser simply stops reading there and is
/// version-agnostic for the parts it consumes.
/// </summary>
public static class MdlParser
{
    public const uint VersionV5 = 0x0100_0005;
    public const uint VersionV6 = 0x0100_0006;

    private const int VertexElementsPerDeclaration = 17;

    private enum VertexType : byte
    {
        Single1 = 0,
        Single2 = 1,
        Single3 = 2,
        Single4 = 3,
        UInt = 5,
        ByteFloat4 = 8,
        Half2 = 13,
        Half4 = 14,
        UByte8 = 17,
    }

    private enum VertexUsage : byte
    {
        Position = 0,
        BlendWeights = 1,
        BlendIndices = 2,
        Normal = 3,
        Uv = 4,
        Tangent2 = 5,
        Tangent1 = 6,
        Color = 7,
    }

    private readonly record struct VertexElement(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    public static ParsedModel Parse(byte[] data)
    {
        var r = new SpanReader(data);

        // --- File header (68 bytes) ---
        var version = r.ReadUInt32();
        if (version is not (VersionV5 or VersionV6))
            throw new NotSupportedException($"Unsupported model version 0x{version:X8}.");
        r.ReadUInt32(); // stack size
        r.ReadUInt32(); // runtime size
        int vertexDeclarationCount = r.ReadUInt16();
        r.ReadUInt16(); // material count (also in model header)
        var vertexOffsets = r.ReadUInt32Array(3);
        var indexOffsets = r.ReadUInt32Array(3);
        r.ReadUInt32Array(3); // vertex buffer sizes
        r.ReadUInt32Array(3); // index buffer sizes
        r.Skip(4); // lod count, index streaming, edge geometry, padding

        // --- Vertex declarations: fixed 17 slots of 8 bytes each ---
        var declarations = new List<VertexElement[]>(vertexDeclarationCount);
        for (var d = 0; d < vertexDeclarationCount; d++)
        {
            var start = r.Position;
            var elements = new List<VertexElement>();
            for (var e = 0; e < VertexElementsPerDeclaration; e++)
            {
                var stream = r.ReadByte();
                var offset = r.ReadByte();
                var type = r.ReadByte();
                var usage = r.ReadByte();
                var usageIndex = r.ReadByte();
                r.Skip(3); // padding
                if (stream == 255)
                    break;
                elements.Add(new VertexElement(stream, offset, type, usage, usageIndex));
            }

            declarations.Add([.. elements]);
            r.Position = start + VertexElementsPerDeclaration * 8;
        }

        // --- String table ---
        var stringCount = r.ReadUInt16();
        r.ReadUInt16();
        var stringSize = (int)r.ReadUInt32();
        var strings = r.ReadBytes(stringSize).ToArray();

        // --- Model header (56 bytes) ---
        var radius = r.ReadSingle();
        int meshCount = r.ReadUInt16();
        int attributeCount = r.ReadUInt16();
        int submeshCount = r.ReadUInt16();
        int materialCount = r.ReadUInt16();
        int boneCount = r.ReadUInt16();
        int boneTableCount = r.ReadUInt16();
        r.ReadUInt16(); // shape count
        r.ReadUInt16(); // shape mesh count
        r.ReadUInt16(); // shape value count
        r.ReadByte(); // lod count
        r.ReadByte(); // flags1
        int elementIdCount = r.ReadUInt16();
        int terrainShadowMeshCount = r.ReadByte();
        var flags2 = r.ReadByte();
        r.ReadSingle(); // model clip-out distance
        r.ReadSingle(); // shadow clip-out distance
        r.ReadUInt16();
        int terrainShadowSubmeshCount = r.ReadUInt16();
        r.ReadUInt32(); // flags3, bg change material indices
        int boneTableArrayCountTotal = r.ReadUInt16();
        r.Skip(56 - 0x2E); // remaining header fields

        var extraLodEnabled = (flags2 & 0x10) != 0;

        var elementIdsRaw = r.ReadBytes(elementIdCount * 32).ToArray();

        // --- LODs (3 entries, 60 bytes each) ---
        var lods = new (ushort MeshIndex, ushort MeshCount)[3];
        var lodRanges = new (float Model, float Texture)[3];
        for (var i = 0; i < 3; i++)
        {
            var start = r.Position;
            lods[i] = (r.ReadUInt16(), r.ReadUInt16());
            lodRanges[i] = (r.ReadSingle(), r.ReadSingle());
            r.Position = start + 60;
        }

        if (extraLodEnabled)
            r.Skip(3 * 40);

        // --- Meshes ---
        var meshes = new MeshInfo[meshCount];
        for (var i = 0; i < meshCount; i++)
        {
            var m = new MeshInfo
            {
                VertexCount = r.ReadUInt16(),
            };
            r.ReadUInt16(); // padding
            m.IndexCount = r.ReadUInt32();
            m.MaterialIndex = r.ReadUInt16();
            r.ReadUInt16(); // submesh index
            r.ReadUInt16(); // submesh count
            m.BoneTableIndex = r.ReadUInt16();
            m.StartIndex = r.ReadUInt32();
            m.VertexBufferOffsets = r.ReadUInt32Array(3);
            m.VertexBufferStrides = [r.ReadByte(), r.ReadByte(), r.ReadByte()];
            m.VertexStreamCount = r.ReadByte();
            meshes[i] = m;
        }

        var attributeNameOffsets = r.ReadUInt32Array(attributeCount);
        r.Skip(terrainShadowMeshCount * 20);
        r.Skip(submeshCount * 16);
        r.Skip(terrainShadowSubmeshCount * 10);
        var materialNameOffsets = r.ReadUInt32Array(materialCount);
        var boneNameOffsets = r.ReadUInt32Array(boneCount);

        var materialNames = new string[materialCount];
        for (var i = 0; i < materialCount; i++)
            materialNames[i] = ReadCString(strings, (int)materialNameOffsets[i]);

        var boneNames = new string[boneCount];
        for (var i = 0; i < boneCount; i++)
            boneNames[i] = ReadCString(strings, (int)boneNameOffsets[i]);

        // --- Bone tables (this is where v5 and v6 diverge) ---
        var boneTables = new ushort[boneTableCount][];
        if (version == VersionV5)
        {
            for (var t = 0; t < boneTableCount; t++)
            {
                var start = r.Position;
                var indices = new ushort[64];
                for (var i = 0; i < 64; i++)
                    indices[i] = r.ReadUInt16();
                var count = (int)r.ReadUInt32();
                boneTables[t] = indices.AsSpan(0, Math.Min(count, 64)).ToArray();
                r.Position = start + 132;
            }
        }
        else
        {
            // v6: N 4-byte headers (offset in u32 units from that header's own
            // position, count) followed by a shared data area.
            var headerStarts = new int[boneTableCount];
            var offsets = new int[boneTableCount];
            var sizes = new int[boneTableCount];
            for (var t = 0; t < boneTableCount; t++)
            {
                headerStarts[t] = r.Position;
                offsets[t] = r.ReadUInt16();
                sizes[t] = r.ReadUInt16();
            }

            var afterHeaders = r.Position;
            for (var t = 0; t < boneTableCount; t++)
            {
                r.Position = headerStarts[t] + offsets[t] * 4;
                boneTables[t] = new ushort[sizes[t]];
                for (var i = 0; i < sizes[t]; i++)
                    boneTables[t][i] = r.ReadUInt16();
            }

            r.Position = afterHeaders + boneTableArrayCountTotal * 2;
        }

        // --- Decode geometry for LOD 0 ---
        var (lodMeshIndex, lodMeshCount) = lods[0];
        var parsedMeshes = new List<ParsedMesh>();
        for (var mi = lodMeshIndex; mi < lodMeshIndex + lodMeshCount && mi < meshCount; mi++)
        {
            var mesh = meshes[mi];
            var decl = declarations[mi];
            var vertices = DecodeVertices(data, mesh, decl, vertexOffsets[0]);
            var indices = DecodeIndices(data, mesh, indexOffsets[0]);
            parsedMeshes.Add(new ParsedMesh
            {
                Vertices = vertices,
                Indices = indices,
                MaterialIndex = mesh.MaterialIndex,
                MaterialName = mesh.MaterialIndex < materialNames.Length ? materialNames[mesh.MaterialIndex] : "",
                BoneTableIndex = mesh.BoneTableIndex,
            });
        }

        return new ParsedModel
        {
            Meshes = parsedMeshes,
            MaterialNames = materialNames,
            BoneNames = boneNames,
            BoneTables = boneTables,
            EditData = new MdlEditData
            {
                Version = version,
                Radius = radius,
                StringCount = stringCount,
                StringsRaw = strings,
                AttributeNameOffsets = attributeNameOffsets,
                MaterialNameOffsets = materialNameOffsets,
                BoneNameOffsets = boneNameOffsets,
                ElementIdsRaw = elementIdsRaw,
                Lod0ModelRange = lodRanges[0].Model,
                Lod0TextureRange = lodRanges[0].Texture,
            },
        };
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

    private static ParsedVertex[] DecodeVertices(byte[] data, MeshInfo mesh, VertexElement[] decl, uint lodVertexOffset)
    {
        var vertices = new ParsedVertex[mesh.VertexCount];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i].Color = new Vector4(1, 1, 1, 1);
            vertices[i].BlendWeights = new Vector4(1, 0, 0, 0);
        }

        foreach (var element in decl)
        {
            if (element.Stream >= mesh.VertexStreamCount)
                continue;
            var usage = (VertexUsage)element.Usage;
            if (usage is not (VertexUsage.Position or VertexUsage.Normal or VertexUsage.Uv
                or VertexUsage.Tangent1 or VertexUsage.Color
                or VertexUsage.BlendWeights or VertexUsage.BlendIndices))
                continue;

            // TexTools-written models declare second channels (UV2, vertex
            // color 2) as extra elements of the same usage with usage index 1.
            // v1 consumes only the first channel; without this check the
            // second channel would overwrite it (UV2 is often a constant,
            // which flattened every texture lookup to one texel).
            if (element.UsageIndex != 0)
                continue;

            var stride = mesh.VertexBufferStrides[element.Stream];
            var streamBase = (int)(lodVertexOffset + mesh.VertexBufferOffsets[element.Stream]);

            for (var i = 0; i < vertices.Length; i++)
            {
                var r = new SpanReader(data) { Position = streamBase + i * stride + element.Offset };
                var value = ReadElement(ref r, (VertexType)element.Type);
                switch (usage)
                {
                    case VertexUsage.Position:
                        vertices[i].Position = new Vector3(value.X, value.Y, value.Z);
                        break;
                    case VertexUsage.Normal:
                        vertices[i].Normal = new Vector3(value.X, value.Y, value.Z);
                        break;
                    case VertexUsage.Uv:
                        // Half4/Single4 UVs pack a second UV channel in Z/W; v1 uses only the first.
                        vertices[i].Uv = new Vector2(value.X, value.Y);
                        break;
                    case VertexUsage.Tangent1:
                        vertices[i].Tangent = value * 2f - new Vector4(1f);
                        break;
                    case VertexUsage.Color:
                        vertices[i].Color = value;
                        break;
                    case VertexUsage.BlendWeights:
                        // UInt/UByte8-declared weights are raw bytes; normalize to 0..1.
                        vertices[i].BlendWeights = (VertexType)element.Type is VertexType.UInt or VertexType.UByte8
                            ? value / 255f
                            : value;
                        break;
                    case VertexUsage.BlendIndices:
                        // Bone table indices as raw bytes; ByteFloat4-declared
                        // indices come back normalized and must be rescaled.
                        var idx = (VertexType)element.Type == VertexType.ByteFloat4 ? value * 255f : value;
                        vertices[i].BlendIndicesPacked =
                            (uint)MathF.Round(idx.X) | ((uint)MathF.Round(idx.Y) << 8)
                            | ((uint)MathF.Round(idx.Z) << 16) | ((uint)MathF.Round(idx.W) << 24);
                        break;
                }
            }
        }

        return vertices;
    }

    private static Vector4 ReadElement(ref SpanReader r, VertexType type) => type switch
    {
        VertexType.Single1 => new Vector4(r.ReadSingle(), 0, 0, 0),
        VertexType.Single2 => new Vector4(r.ReadSingle(), r.ReadSingle(), 0, 0),
        VertexType.Single3 => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 0),
        VertexType.Single4 => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        VertexType.UInt => new Vector4(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        VertexType.ByteFloat4 => new Vector4(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()) / 255f,
        VertexType.Half2 => new Vector4(r.ReadHalf(), r.ReadHalf(), 0, 0),
        VertexType.Half4 => new Vector4(r.ReadHalf(), r.ReadHalf(), r.ReadHalf(), r.ReadHalf()),
        VertexType.UByte8 => new Vector4(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        _ => throw new NotSupportedException($"Unsupported vertex element type {type}."),
    };

    private static uint[] DecodeIndices(byte[] data, MeshInfo mesh, uint lodIndexOffset)
    {
        var r = new SpanReader(data) { Position = (int)(lodIndexOffset + mesh.StartIndex * 2) };
        var indices = new uint[mesh.IndexCount];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = r.ReadUInt16();
        return indices;
    }

    private sealed class MeshInfo
    {
        public ushort VertexCount;
        public uint IndexCount;
        public ushort MaterialIndex;
        public ushort BoneTableIndex;
        public uint StartIndex;
        public uint[] VertexBufferOffsets = [];
        public byte[] VertexBufferStrides = [];
        public byte VertexStreamCount;
    }
}

/// <summary>Geometry decoded from a .mdl file, one entry per LOD-0 mesh.</summary>
public sealed class ParsedModel
{
    public required IReadOnlyList<ParsedMesh> Meshes { get; init; }

    /// <summary>Material names as stored in the model (e.g. "/mt_w0201b0001_a.mtrl").</summary>
    public required IReadOnlyList<string> MaterialNames { get; init; }

    /// <summary>All bone names referenced by the model, in bone-list order.</summary>
    public IReadOnlyList<string> BoneNames { get; init; } = [];

    /// <summary>Per-table lists of bone-list indices; vertex blend indices index into their mesh's table.</summary>
    public IReadOnlyList<ushort[]> BoneTables { get; init; } = [];

    /// <summary>Extra data preserved for rewriting the model (see MdlWriter). Null when parsed without edit support.</summary>
    public MdlEditData? EditData { get; init; }
}

/// <summary>Original-file blocks the writer reuses verbatim so string offsets stay valid.</summary>
public sealed class MdlEditData
{
    public required uint Version { get; init; }

    public required float Radius { get; init; }

    public required ushort StringCount { get; init; }

    public required byte[] StringsRaw { get; init; }

    public required uint[] AttributeNameOffsets { get; init; }

    public required uint[] MaterialNameOffsets { get; init; }

    public required uint[] BoneNameOffsets { get; init; }

    public required byte[] ElementIdsRaw { get; init; }

    public required float Lod0ModelRange { get; init; }

    public required float Lod0TextureRange { get; init; }
}

public sealed class ParsedMesh
{
    public required ParsedVertex[] Vertices { get; init; }

    public required uint[] Indices { get; init; }

    public required string MaterialName { get; init; }

    public int MaterialIndex { get; init; }

    public int BoneTableIndex { get; init; }
}

public struct ParsedVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Tangent;
    public Vector4 Color;

    /// <summary>Skin weights for up to four influences.</summary>
    public Vector4 BlendWeights;

    /// <summary>Four bone-table indices packed little-endian (byte 0 = influence 0).</summary>
    public uint BlendIndicesPacked;

    public readonly byte BlendIndex(int influence) => (byte)(BlendIndicesPacked >> (influence * 8));
}
