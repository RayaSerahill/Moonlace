using System.Numerics;
using Moonlace.GameData.Parsing;
using SharpGLTF.Schema2;

namespace Moonlace.GameData.Interchange;

/// <summary>
/// Imports a GLTF/GLB as a replacement for an existing FFXIV model. The
/// original parsed model acts as the template: material slots are mapped by
/// material name (falling back to primitive order when unambiguous), skin
/// weights are remapped through joint names onto the template's bone list,
/// and per-mesh bone tables are extended as needed (up to the format's 64).
/// </summary>
public static class GltfImporter
{
    public static ModelImportResult Import(string path, ParsedModel template)
    {
        ModelRoot gltf;
        try
        {
            gltf = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix });
        }
        catch (Exception ex)
        {
            throw new ModelImportException(
                $"\"{Path.GetFileName(path)}\" could not be read as a GLTF/GLB file: {ex.Message}", ex);
        }

        var primitives = gltf.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives.Select(p => (Mesh: mesh, Primitive: p)))
            .ToList();
        if (primitives.Count == 0)
            throw new ModelImportException("The file contains no meshes.");

        // Joint index (per skin) → template bone-list index, resolved by name.
        var boneIndexByName = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (var i = 0; i < template.BoneNames.Count; i++)
            boneIndexByName[template.BoneNames[i]] = (ushort)i;

        var skinMaps = new Dictionary<Skin, ushort[]>();
        foreach (var skin in gltf.LogicalSkins)
        {
            var map = new ushort[skin.JointsCount];
            for (var j = 0; j < skin.JointsCount; j++)
            {
                var jointName = skin.GetJoint(j).Joint.Name ?? $"joint_{j}";
                if (!boneIndexByName.TryGetValue(jointName, out var boneIndex))
                    throw new ModelImportException(
                        $"The model is weighted to bone \"{jointName}\", which does not exist in the original " +
                        "FFXIV model. Keep the vertex groups that came with the exported model.");
                map[j] = boneIndex;
            }

            skinMaps[skin] = map;
        }

        // Node lookup: which skin is used to render each mesh.
        var skinByMesh = new Dictionary<Mesh, Skin>();
        foreach (var node in gltf.LogicalNodes)
        {
            if (node.Mesh is not null && node.Skin is not null)
                skinByMesh[node.Mesh] = node.Skin;
        }

        var boneTables = template.BoneTables.Select(t => t.ToList()).ToList();
        if (boneTables.Count == 0)
            boneTables.Add([]);

        var meshes = new List<ParsedMesh>();
        for (var pi = 0; pi < primitives.Count; pi++)
        {
            var (mesh, primitive) = primitives[pi];
            var label = primitive.Material?.Name is { Length: > 0 } n ? n : (mesh.Name ?? $"primitive {pi}");

            if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                throw new ModelImportException(
                    $"\"{label}\" uses {primitive.DrawPrimitiveType}; only triangle meshes are supported.");

            var materialIndex = ModelImportShared.ResolveMaterialIndex(
                primitive.Material?.Name, pi, primitives.Count, template, label);
            var templateMesh = pi < template.Meshes.Count ? template.Meshes[pi] : template.Meshes[0];
            var boneTableIndex = Math.Min(templateMesh.BoneTableIndex, boneTables.Count - 1);

            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
                ?? throw new ModelImportException($"\"{label}\" has no vertex positions.");
            if (positions.Count == 0)
                throw new ModelImportException($"\"{label}\" has no vertices.");
            if (positions.Count > ushort.MaxValue)
                throw new ModelImportException(
                    $"\"{label}\" has {positions.Count:N0} vertices; FFXIV models support at most 65,535 per mesh. " +
                    "Split the mesh or reduce its vertex count.");

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            if (normals is null)
                throw new ModelImportException(
                    $"\"{label}\" has no normals. Export from Blender with normals enabled.");

            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array()
                ?? throw new ModelImportException(
                    $"\"{label}\" has no UV coordinates (TEXCOORD_0), which FFXIV materials require.");

            var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
            var colors = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray();
            var jointsAccessor = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var weightsAccessor = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

            var skinMap = skinByMesh.TryGetValue(mesh, out var skin) ? skinMaps[skin] : null;
            var table = boneTables[boneTableIndex];

            var vertices = new ParsedVertex[positions.Count];
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = new ParsedVertex
                {
                    Position = positions[i],
                    Normal = normals[i],
                    Uv = uvs[i],
                    Tangent = tangents is not null ? tangents[i] : Vector4.Zero,
                    Color = colors is not null ? colors[i] : new Vector4(1, 1, 1, 1),
                };

                (v.BlendWeights, v.BlendIndicesPacked) = MapSkinning(
                    jointsAccessor?[i], weightsAccessor?[i], skinMap, table, label);
                vertices[i] = v;
            }

            var indexAccessor = primitive.GetIndexAccessor()
                ?? throw new ModelImportException($"\"{label}\" has no triangle indices.");
            var indices = indexAccessor.AsIndicesArray().ToArray();
            if (indices.Length == 0 || indices.Length % 3 != 0)
                throw new ModelImportException($"\"{label}\" has a malformed triangle list ({indices.Length} indices).");
            if (indices.Max() >= vertices.Length)
                throw new ModelImportException($"\"{label}\" has indices pointing beyond its vertex data.");

            meshes.Add(new ParsedMesh
            {
                Vertices = vertices,
                Indices = indices,
                MaterialIndex = materialIndex,
                MaterialName = template.MaterialNames[materialIndex],
                BoneTableIndex = boneTableIndex,
            });
        }

        return new ModelImportResult(meshes, boneTables.Select(t => t.ToArray()).ToArray());
    }

    private static (Vector4 Weights, uint IndicesPacked) MapSkinning(
        Vector4? joints, Vector4? weights, ushort[]? skinMap, List<ushort> boneTable, string label)
    {
        if (joints is null || weights is null || skinMap is null)
        {
            ModelImportShared.EnsureInTable(boneTable, 0, label);
            return (new Vector4(1, 0, 0, 0), (uint)ModelImportShared.IndexInTable(boneTable, 0));
        }

        var j = joints.Value;
        var w = weights.Value;
        Span<float> outWeights = stackalloc float[4];
        Span<byte> outIndices = stackalloc byte[4];
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = influence switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
            var joint = (int)(influence switch { 0 => j.X, 1 => j.Y, 2 => j.Z, _ => j.W });
            if (weight <= 0 || joint >= skinMap.Length)
                continue;

            var boneIndex = skinMap[joint];
            ModelImportShared.EnsureInTable(boneTable, boneIndex, label);
            outWeights[influence] = weight;
            outIndices[influence] = (byte)ModelImportShared.IndexInTable(boneTable, boneIndex);
        }

        var sum = outWeights[0] + outWeights[1] + outWeights[2] + outWeights[3];
        if (sum <= 0)
        {
            ModelImportShared.EnsureInTable(boneTable, 0, label);
            return (new Vector4(1, 0, 0, 0), (uint)ModelImportShared.IndexInTable(boneTable, 0));
        }

        var packed = (uint)outIndices[0] | ((uint)outIndices[1] << 8) | ((uint)outIndices[2] << 16) | ((uint)outIndices[3] << 24);
        return (new Vector4(outWeights[0], outWeights[1], outWeights[2], outWeights[3]) / sum, packed);
    }

}
