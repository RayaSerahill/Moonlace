using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Moonlace.GameData.Parsing;
using Silk.NET.Assimp;
using AiFace = Silk.NET.Assimp.Face;
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using AiTexture = Silk.NET.Assimp.Texture;

namespace Moonlace.GameData.Interchange;

/// <summary>
/// Exports the effective model as a binary FBX aimed at Blender interop,
/// mirroring <see cref="GltfExporter"/>: geometry, normals, tangents, UVs,
/// vertex colors, skin weights with named joints, material slots named after
/// the FFXIV materials, and embedded base-color/normal textures.
///
/// FBX conventions differ from GLTF in two ways handled here: geometry is
/// written in centimeters (the Blender and TexTools convention — FFXIV
/// meters × 100 with the default UnitScaleFactor of 1), and the UV origin
/// is bottom-left, so V is flipped on the way out and back on import.
///
/// The scene is assembled through assimp's C API, so everything lives in
/// manually allocated memory for the duration of the export call.
/// </summary>
public static class FbxExporter
{
    internal const float MetersToFbxUnits = 100f;

    public static unsafe void Export(ParsedModel model, IReadOnlyList<ModelMaterialInfo> materials, string outputPath)
    {
        using var arena = new NativeArena();

        // Joints: only bones actually referenced by weights become nodes —
        // assimp turns bone-referenced nodes into FBX limb nodes (Blender
        // armature bones) and everything else into clutter empties.
        var usedBones = CollectUsedBones(model);

        var root = arena.Alloc<AiNode>();
        root->MName = AiString("n_root");
        root->MTransformation = Matrix4x4.Identity;

        var children = new List<nint>();
        var jointNodes = new Dictionary<int, nint>();
        foreach (var boneIndex in usedBones)
        {
            var joint = arena.Alloc<AiNode>();
            joint->MName = AiString(model.BoneNames[boneIndex]);
            joint->MTransformation = Matrix4x4.Identity;
            joint->MParent = root;
            jointNodes[boneIndex] = (nint)joint;
            children.Add((nint)joint);
        }

        var meshPointers = new List<nint>();
        for (var meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
        {
            var mesh = BuildMesh(arena, model, meshIndex, materials.Count);
            meshPointers.Add((nint)mesh);

            var meshNode = arena.Alloc<AiNode>();
            meshNode->MName = mesh->MName;
            meshNode->MTransformation = Matrix4x4.Identity;
            meshNode->MParent = root;
            var indices = arena.Alloc<uint>(1);
            *indices = (uint)meshIndex;
            meshNode->MNumMeshes = 1;
            meshNode->MMeshes = indices;
            children.Add((nint)meshNode);
        }

        var childArray = arena.Alloc<nint>(children.Count);
        for (var i = 0; i < children.Count; i++)
            childArray[i] = children[i];
        root->MNumChildren = (uint)children.Count;
        root->MChildren = (AiNode**)childArray;

        var textures = new List<nint>();
        var materialPointers = new List<nint>();
        foreach (var material in materials)
            materialPointers.Add((nint)BuildMaterial(arena, material, textures));
        materialPointers.Add((nint)BuildFallbackMaterial(arena));

        var scene = arena.Alloc<AiScene>();
        scene->MName = AiString("moonlace");
        scene->MRootNode = root;
        scene->MNumMeshes = (uint)meshPointers.Count;
        scene->MMeshes = (AiMesh**)arena.AllocArray(meshPointers);
        scene->MNumMaterials = (uint)materialPointers.Count;
        scene->MMaterials = (AiMaterial**)arena.AllocArray(materialPointers);
        if (textures.Count > 0)
        {
            scene->MNumTextures = (uint)textures.Count;
            scene->MTextures = (AiTexture**)arena.AllocArray(textures);
        }

        var result = AssimpNative.Api.ExportScene(scene, "fbx", outputPath, 0);
        if (result != Return.Success)
            throw new InvalidOperationException(
                $"FBX export failed: {AssimpNative.Api.GetErrorStringS()}");
    }

    private static SortedSet<int> CollectUsedBones(ParsedModel model)
    {
        var used = new SortedSet<int>();
        foreach (var mesh in model.Meshes)
        {
            var boneTable = mesh.BoneTableIndex < model.BoneTables.Count
                ? model.BoneTables[mesh.BoneTableIndex]
                : [];
            if (boneTable.Length == 0)
                continue;

            foreach (var vertex in mesh.Vertices)
            {
                foreach (var (joint, weight) in VertexInfluences(vertex, boneTable, model.BoneNames.Count))
                {
                    if (weight > 0)
                        used.Add(joint);
                }
            }

            // The fallback binding for weightless vertices (see BuildMesh).
            if (boneTable[0] < model.BoneNames.Count)
                used.Add(boneTable[0]);
        }

        return used;
    }

    private static IEnumerable<(int Joint, float Weight)> VertexInfluences(
        ParsedVertex vertex, ushort[] boneTable, int boneCount)
    {
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = influence switch
            {
                0 => vertex.BlendWeights.X,
                1 => vertex.BlendWeights.Y,
                2 => vertex.BlendWeights.Z,
                _ => vertex.BlendWeights.W,
            };
            if (weight <= 0)
                continue;

            int tableIndex = vertex.BlendIndex(influence);
            var joint = tableIndex < boneTable.Length ? boneTable[tableIndex] : 0;
            if (joint < boneCount)
                yield return (joint, weight);
        }
    }

    private static unsafe AiMesh* BuildMesh(NativeArena arena, ParsedModel model, int meshIndex, int materialCount)
    {
        var source = model.Meshes[meshIndex];
        var boneTable = source.BoneTableIndex < model.BoneTables.Count
            ? model.BoneTables[source.BoneTableIndex]
            : [];

        var mesh = arena.Alloc<AiMesh>();
        mesh->MName = AiString($"mesh_{meshIndex}");
        mesh->MPrimitiveTypes = (uint)PrimitiveType.Triangle;
        mesh->MMaterialIndex = source.MaterialIndex >= 0 && source.MaterialIndex < materialCount
            ? (uint)source.MaterialIndex
            : (uint)materialCount; // the fallback material appended after the real ones

        var count = source.Vertices.Length;
        mesh->MNumVertices = (uint)count;
        var positions = arena.Alloc<Vector3>(count);
        var normals = arena.Alloc<Vector3>(count);
        var tangents = arena.Alloc<Vector3>(count);
        var bitangents = arena.Alloc<Vector3>(count);
        var uvs = arena.Alloc<Vector3>(count);
        var colors = arena.Alloc<Vector4>(count);
        for (var i = 0; i < count; i++)
        {
            var v = source.Vertices[i];
            positions[i] = v.Position * MetersToFbxUnits;
            var normal = v.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
            normals[i] = normal;
            var (tangent, w) = OrthonormalTangent(normal, v.Tangent);
            tangents[i] = tangent;
            bitangents[i] = Vector3.Cross(normal, tangent) * w;
            uvs[i] = new Vector3(v.Uv.X, 1f - v.Uv.Y, 0);
            colors[i] = v.Color;
        }

        mesh->MVertices = positions;
        mesh->MNormals = normals;
        mesh->MTangents = tangents;
        mesh->MBitangents = bitangents;
        mesh->MTextureCoords.Element0 = uvs;
        mesh->MNumUVComponents[0] = 2;
        mesh->MColors.Element0 = colors;

        var faceCount = source.Indices.Length / 3;
        mesh->MNumFaces = (uint)faceCount;
        var faces = arena.Alloc<AiFace>(faceCount);
        for (var f = 0; f < faceCount; f++)
        {
            var indices = arena.Alloc<uint>(3);
            indices[0] = source.Indices[f * 3];
            indices[1] = source.Indices[f * 3 + 1];
            indices[2] = source.Indices[f * 3 + 2];
            faces[f].MNumIndices = 3;
            faces[f].MIndices = indices;
        }

        mesh->MFaces = faces;

        if (boneTable.Length > 0 && model.BoneNames.Count > 0)
            BuildBones(arena, mesh, source, boneTable, model);

        return mesh;
    }

    private static unsafe void BuildBones(
        NativeArena arena, AiMesh* mesh, ParsedMesh source, ushort[] boneTable, ParsedModel model)
    {
        var weightsByBone = new Dictionary<int, List<VertexWeight>>();
        for (var i = 0; i < source.Vertices.Length; i++)
        {
            var bound = false;
            foreach (var (joint, weight) in VertexInfluences(source.Vertices[i], boneTable, model.BoneNames.Count))
            {
                Weights(joint).Add(new VertexWeight { MVertexId = (uint)i, MWeight = weight });
                bound = true;
            }

            if (!bound && boneTable[0] < model.BoneNames.Count)
                Weights(boneTable[0]).Add(new VertexWeight { MVertexId = (uint)i, MWeight = 1f });
        }

        if (weightsByBone.Count == 0)
            return;

        var bones = new List<nint>();
        foreach (var (joint, weights) in weightsByBone.OrderBy(p => p.Key))
        {
            var bone = arena.Alloc<Bone>();
            bone->MName = AiString(model.BoneNames[joint]);
            bone->MOffsetMatrix = Matrix4x4.Identity;
            bone->MNumWeights = (uint)weights.Count;
            var array = arena.Alloc<VertexWeight>(weights.Count);
            for (var w = 0; w < weights.Count; w++)
                array[w] = weights[w];
            bone->MWeights = array;
            bones.Add((nint)bone);
        }

        mesh->MNumBones = (uint)bones.Count;
        mesh->MBones = (Bone**)arena.AllocArray(bones);

        List<VertexWeight> Weights(int joint)
            => weightsByBone.TryGetValue(joint, out var list) ? list : weightsByBone[joint] = [];
    }

    private static (Vector3 Tangent, float W) OrthonormalTangent(Vector3 normal, Vector4 sourceTangent)
    {
        var tangent = new Vector3(sourceTangent.X, sourceTangent.Y, sourceTangent.Z);
        if (tangent.LengthSquared() > 1e-6f)
        {
            tangent = tangent - normal * Vector3.Dot(normal, tangent);
            if (tangent.LengthSquared() > 1e-6f)
                return (Vector3.Normalize(tangent), sourceTangent.W >= 0 ? 1f : -1f);
        }

        var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        return (Vector3.Normalize(Vector3.Cross(reference, normal)), 1f);
    }

    private static unsafe AiMaterial* BuildMaterial(
        NativeArena arena, ModelMaterialInfo info, List<nint> textures)
    {
        var properties = new List<nint> { (nint)StringProperty(arena, "?mat.name", 0, 0, info.Name) };

        if (info.BaseColorPng is not null)
        {
            properties.Add((nint)StringProperty(
                arena, "$tex.file", (uint)TextureType.Diffuse, 0, EmbedTexture(arena, textures, info.BaseColorPng)));
        }

        if (info.NormalPng is not null)
        {
            properties.Add((nint)StringProperty(
                arena, "$tex.file", (uint)TextureType.Normals, 0, EmbedTexture(arena, textures, info.NormalPng)));
        }

        return FinishMaterial(arena, properties);
    }

    private static unsafe AiMaterial* BuildFallbackMaterial(NativeArena arena)
        => FinishMaterial(arena, [(nint)StringProperty(arena, "?mat.name", 0, 0, "moonlace_fallback")]);

    private static unsafe AiMaterial* FinishMaterial(NativeArena arena, List<nint> properties)
    {
        var material = arena.Alloc<AiMaterial>();
        material->MProperties = (MaterialProperty**)arena.AllocArray(properties);
        material->MNumProperties = (uint)properties.Count;
        material->MNumAllocated = (uint)properties.Count;
        return material;
    }

    /// <summary>Adds a PNG as a scene-embedded texture and returns its "*N" reference.</summary>
    private static unsafe string EmbedTexture(NativeArena arena, List<nint> textures, byte[] png)
    {
        var texture = arena.Alloc<AiTexture>();
        texture->MWidth = (uint)png.Length;
        texture->MHeight = 0; // compressed data, width = byte length
        texture->AchFormatHint[0] = (byte)'p';
        texture->AchFormatHint[1] = (byte)'n';
        texture->AchFormatHint[2] = (byte)'g';
        var data = arena.Alloc<byte>(png.Length);
        png.CopyTo(new Span<byte>(data, png.Length));
        texture->PcData = (Texel*)data;

        textures.Add((nint)texture);
        return $"*{textures.Count - 1}";
    }

    /// <summary>A material string property; the payload is a serialized aiString (length prefix + bytes + NUL).</summary>
    private static unsafe MaterialProperty* StringProperty(
        NativeArena arena, string key, uint semantic, uint index, string value)
    {
        var property = arena.Alloc<MaterialProperty>();
        property->MKey = AiString(key);
        property->MSemantic = semantic;
        property->MIndex = index;
        property->MType = PropertyTypeInfo.String;

        var bytes = Encoding.UTF8.GetBytes(value);
        var data = arena.Alloc<byte>(4 + bytes.Length + 1);
        *(int*)data = bytes.Length;
        bytes.CopyTo(new Span<byte>(data + 4, bytes.Length));
        property->MDataLength = (uint)(4 + bytes.Length + 1);
        property->MData = data;
        return property;
    }

    private static unsafe AssimpString AiString(string value)
    {
        var result = default(AssimpString);
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = Math.Min(bytes.Length, 1023);
        result.Length = (uint)length;
        for (var i = 0; i < length; i++)
            result.Data[i] = bytes[i];
        return result;
    }

    /// <summary>Zero-initialized unmanaged allocations freed together when the export ends.</summary>
    private sealed unsafe class NativeArena : IDisposable
    {
        private readonly List<nint> _allocations = [];

        public T* Alloc<T>(int count = 1) where T : unmanaged
        {
            var pointer = Marshal.AllocHGlobal(sizeof(T) * count);
            _allocations.Add(pointer);
            new Span<byte>((void*)pointer, sizeof(T) * count).Clear();
            return (T*)pointer;
        }

        public nint* AllocArray(List<nint> pointers)
        {
            var array = Alloc<nint>(pointers.Count);
            for (var i = 0; i < pointers.Count; i++)
                array[i] = pointers[i];
            return array;
        }

        public void Dispose()
        {
            foreach (var pointer in _allocations)
                Marshal.FreeHGlobal(pointer);
            _allocations.Clear();
        }
    }
}
