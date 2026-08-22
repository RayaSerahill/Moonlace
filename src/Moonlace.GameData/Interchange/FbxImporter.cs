using System.Numerics;
using System.Text;
using Moonlace.GameData.Parsing;
using Silk.NET.Assimp;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;

namespace Moonlace.GameData.Interchange;

/// <summary>
/// Imports an FBX file as a replacement for an existing FFXIV model,
/// mirroring <see cref="GltfImporter"/>: the original parsed model acts as
/// the template, material slots are mapped by material name, skin weights
/// are remapped through bone names onto the template's bone list, and
/// per-mesh bone tables are extended as needed (up to the format's 64).
/// Part-named meshes ("mesh_2.1") are regrouped into their FFXIV mesh with
/// the submesh partition — and the template's attribute masks — restored.
///
/// FBX specifics: assimp triangulates and merges duplicate corners, node
/// transforms are baked into the geometry, coordinates are converted from
/// FBX units to meters via the file's UnitScaleFactor (centimeters when
/// unspecified), and the bottom-left UV origin is flipped back.
/// </summary>
public static class FbxImporter
{
    public static unsafe ModelImportResult Import(string path, ParsedModel template)
    {
        var ai = AssimpNative.Api;
        var scene = ai.ImportFile(path, (uint)(
            PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.SortByPrimitiveType));
        if (scene == null)
            throw new ModelImportException(
                $"\"{Path.GetFileName(path)}\" could not be read as an FBX file: {ai.GetErrorStringS()}");

        try
        {
            return Convert(scene, template);
        }
        finally
        {
            ai.ReleaseImport(scene);
        }
    }

    private static unsafe ModelImportResult Convert(AiScene* scene, ParsedModel template)
    {
        var unitsToMeters = UnitScaleFactor(scene) / FbxExporter.MetersToFbxUnits;

        var triangleMeshes = new List<(nint Mesh, Matrix4x4 Transform)>();
        var transforms = new Dictionary<nint, Matrix4x4>();
        CollectMeshTransforms(scene->MRootNode, Matrix4x4.Identity, transforms);
        for (var i = 0; i < scene->MNumMeshes; i++)
        {
            var mesh = scene->MMeshes[i];
            if (mesh->MPrimitiveTypes != (uint)PrimitiveType.Triangle || mesh->MNumFaces == 0)
                continue; // stray points/lines split out by SortByPrimitiveType
            triangleMeshes.Add(((nint)mesh,
                transforms.TryGetValue((nint)i, out var transform) ? transform : Matrix4x4.Identity));
        }

        if (triangleMeshes.Count == 0)
            throw new ModelImportException("The file contains no meshes.");

        var boneIndexByName = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (var i = 0; i < template.BoneNames.Count; i++)
            boneIndexByName[template.BoneNames[i]] = (ushort)i;

        var boneTables = template.BoneTables.Select(t => t.ToList()).ToList();
        if (boneTables.Count == 0)
            boneTables.Add([]);

        // Group meshes into FFXIV meshes: part-named meshes ("mesh_2.1",
        // written by the exporter per submesh) regroup by mesh number so the
        // partition and its attributes survive; other names import whole.
        var groups = new List<(int? TemplateMeshIndex, List<(nint Mesh, Matrix4x4 Transform, int PartNumber)> Parts)>();
        var groupByMeshNumber = new Dictionary<int, int>();
        foreach (var (meshPointer, transform) in triangleMeshes)
        {
            var name = ReadString(((AiMesh*)meshPointer)->MName);
            if (ModelImportShared.TryParsePartName(name, out var meshNumber, out var partNumber))
            {
                if (!groupByMeshNumber.TryGetValue(meshNumber, out var g))
                {
                    groupByMeshNumber[meshNumber] = g = groups.Count;
                    groups.Add((meshNumber, []));
                }

                groups[g].Parts.Add((meshPointer, transform, partNumber));
            }
            else
            {
                groups.Add((null, [(meshPointer, transform, -1)]));
            }
        }

        var meshes = new List<ParsedMesh>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var (templateMeshIndex, parts) = groups[gi];
            var firstMesh = (AiMesh*)parts[0].Mesh;
            var groupMaterialName = MaterialName(scene, firstMesh->MMaterialIndex);
            var groupLabel = groupMaterialName
                ?? (firstMesh->MName.Length > 0 ? ReadString(firstMesh->MName) : $"mesh {gi}");

            var materialIndex = ModelImportShared.ResolveMaterialIndex(
                groupMaterialName, gi, groups.Count, template, groupLabel);
            var templateMesh = templateMeshIndex is { } tmi && tmi < template.Meshes.Count
                ? template.Meshes[tmi]
                : gi < template.Meshes.Count ? template.Meshes[gi] : template.Meshes[0];
            var boneTableIndex = Math.Min(templateMesh.BoneTableIndex, boneTables.Count - 1);
            var table = boneTables[boneTableIndex];

            var importedParts = new List<ModelImportShared.ImportedPart>();
            foreach (var (meshPointer, transform, partNumber) in parts)
            {
                var mesh = (AiMesh*)meshPointer;
                var materialName = MaterialName(scene, mesh->MMaterialIndex);
                var label = materialName ?? (mesh->MName.Length > 0 ? ReadString(mesh->MName) : groupLabel);
                if (materialName is not null
                    && ModelImportShared.ResolveMaterialIndex(materialName, gi, groups.Count, template, label) != materialIndex)
                    throw new ModelImportException(
                        $"The parts of \"{groupLabel}\" use different materials; an FFXIV mesh has exactly one. " +
                        "Give all parts of a mesh the same material.");

                var (vertices, indices) = ImportMesh(mesh, transform, table, label);
                importedParts.Add(new ModelImportShared.ImportedPart(vertices, indices, partNumber, label));
            }

            meshes.Add(ModelImportShared.MergeParts(
                importedParts,
                templateMeshIndex is not null ? templateMesh : null,
                materialIndex, template.MaterialNames[materialIndex], boneTableIndex));
        }

        return new ModelImportResult(meshes, boneTables.Select(t => t.ToArray()).ToArray());

        (ParsedVertex[] Vertices, uint[] Indices) ImportMesh(
            AiMesh* mesh, Matrix4x4 transform, List<ushort> table, string label)
        {
            var count = (int)mesh->MNumVertices;
            if (count == 0)
                throw new ModelImportException($"\"{label}\" has no vertices.");
            if (count > ushort.MaxValue)
                throw new ModelImportException(
                    $"\"{label}\" has {count:N0} vertices; FFXIV models support at most 65,535 per mesh. " +
                    "Split the mesh or reduce its vertex count.");
            if (mesh->MNormals == null)
                throw new ModelImportException(
                    $"\"{label}\" has no normals. Export from Blender with normals enabled.");
            if (mesh->MTextureCoords.Element0 == null)
                throw new ModelImportException(
                    $"\"{label}\" has no UV coordinates, which FFXIV materials require.");

            var influences = CollectInfluences(mesh, count, boneIndexByName, label);

            Matrix4x4.Invert(transform, out var inverse);
            var normalTransform = Matrix4x4.Transpose(inverse);

            var vertices = new ParsedVertex[count];
            for (var i = 0; i < count; i++)
            {
                var vertex = new ParsedVertex
                {
                    Position = Vector3.Transform(mesh->MVertices[i], transform) * unitsToMeters,
                    Normal = SafeNormalize(Vector3.TransformNormal(mesh->MNormals[i], normalTransform)),
                    Uv = new Vector2(mesh->MTextureCoords.Element0[i].X, 1f - mesh->MTextureCoords.Element0[i].Y),
                    Tangent = ReadTangent(mesh, i, transform),
                    Color = mesh->MColors.Element0 != null ? mesh->MColors.Element0[i] : new Vector4(1, 1, 1, 1),
                };

                (vertex.BlendWeights, vertex.BlendIndicesPacked) = PackInfluences(influences[i], table, label);
                vertices[i] = vertex;
            }

            var indices = new uint[mesh->MNumFaces * 3];
            for (var f = 0; f < mesh->MNumFaces; f++)
            {
                var face = mesh->MFaces[f];
                if (face.MNumIndices != 3)
                    throw new ModelImportException($"\"{label}\" has a malformed triangle list.");
                for (var c = 0; c < 3; c++)
                {
                    var index = face.MIndices[c];
                    if (index >= count)
                        throw new ModelImportException($"\"{label}\" has indices pointing beyond its vertex data.");
                    indices[f * 3 + c] = index;
                }
            }

            return (vertices, indices);
        }
    }

    /// <summary>Per-vertex bone influences (template bone index + weight) from the mesh's per-bone weight lists.</summary>
    private static unsafe List<(ushort Bone, float Weight)>[] CollectInfluences(
        AiMesh* mesh, int vertexCount, Dictionary<string, ushort> boneIndexByName, string label)
    {
        var influences = new List<(ushort Bone, float Weight)>[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            influences[i] = [];

        for (var b = 0; b < mesh->MNumBones; b++)
        {
            var bone = mesh->MBones[b];
            var name = ReadString(bone->MName);
            if (!boneIndexByName.TryGetValue(name, out var boneIndex))
                throw new ModelImportException(
                    $"The model is weighted to bone \"{name}\", which does not exist in the original " +
                    "FFXIV model. Keep the vertex groups that came with the exported model.");

            for (var w = 0; w < bone->MNumWeights; w++)
            {
                var weight = bone->MWeights[w];
                if (weight.MWeight > 0 && weight.MVertexId < vertexCount)
                    influences[weight.MVertexId].Add((boneIndex, weight.MWeight));
            }
        }

        return influences;
    }

    /// <summary>Keeps the four strongest influences, normalized, packed for the mesh's bone table.</summary>
    private static (Vector4 Weights, uint IndicesPacked) PackInfluences(
        List<(ushort Bone, float Weight)> influences, List<ushort> boneTable, string label)
    {
        if (influences.Count == 0)
        {
            ModelImportShared.EnsureInTable(boneTable, 0, label);
            return (new Vector4(1, 0, 0, 0), (uint)ModelImportShared.IndexInTable(boneTable, 0));
        }

        influences.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        Span<float> weights = stackalloc float[4];
        Span<byte> indices = stackalloc byte[4];
        var sum = 0f;
        var kept = Math.Min(influences.Count, 4);
        for (var i = 0; i < kept; i++)
        {
            var (bone, weight) = influences[i];
            ModelImportShared.EnsureInTable(boneTable, bone, label);
            weights[i] = weight;
            indices[i] = (byte)ModelImportShared.IndexInTable(boneTable, bone);
            sum += weight;
        }

        var packed = (uint)indices[0] | ((uint)indices[1] << 8) | ((uint)indices[2] << 16) | ((uint)indices[3] << 24);
        return (new Vector4(weights[0], weights[1], weights[2], weights[3]) / sum, packed);
    }

    private static unsafe Vector4 ReadTangent(AiMesh* mesh, int index, Matrix4x4 transform)
    {
        if (mesh->MTangents == null || mesh->MBitangents == null)
            return Vector4.Zero;

        var tangent = Vector3.TransformNormal(mesh->MTangents[index], transform);
        if (tangent.LengthSquared() < 1e-10f)
            return Vector4.Zero;

        tangent = Vector3.Normalize(tangent);
        var bitangent = Vector3.TransformNormal(mesh->MBitangents[index], transform);
        var normal = Vector3.TransformNormal(mesh->MNormals[index], transform);
        var w = Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) >= 0 ? 1f : -1f;
        return new Vector4(tangent, w);
    }

    private static Vector3 SafeNormalize(Vector3 v)
        => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : Vector3.UnitY;

    /// <summary>Global transform per mesh index, in row-vector convention (assimp matrices are transposed).</summary>
    private static unsafe void CollectMeshTransforms(
        AiNode* node, Matrix4x4 parentGlobal, Dictionary<nint, Matrix4x4> transforms)
    {
        if (node == null)
            return;

        var global = Matrix4x4.Transpose(node->MTransformation) * parentGlobal;
        for (var i = 0; i < node->MNumMeshes; i++)
            transforms.TryAdd((nint)node->MMeshes[i], global);
        for (var c = 0; c < node->MNumChildren; c++)
            CollectMeshTransforms(node->MChildren[c], global, transforms);
    }

    /// <summary>FBX units per centimeter; 1 (centimeters) when the file does not say.</summary>
    private static unsafe float UnitScaleFactor(AiScene* scene)
    {
        var metadata = scene->MMetaData;
        if (metadata == null)
            return 1f;

        for (var i = 0; i < metadata->MNumProperties; i++)
        {
            if (ReadString(metadata->MKeys[i]) != "UnitScaleFactor")
                continue;
            var entry = metadata->MValues[i];
            return entry.MType switch
            {
                MetadataType.Float => *(float*)entry.MData,
                MetadataType.Double => (float)*(double*)entry.MData,
                MetadataType.Int32 => *(int*)entry.MData,
                _ => 1f,
            };
        }

        return 1f;
    }

    private static unsafe string? MaterialName(AiScene* scene, uint materialIndex)
    {
        if (materialIndex >= scene->MNumMaterials)
            return null;

        var material = scene->MMaterials[materialIndex];
        for (var p = 0; p < material->MNumProperties; p++)
        {
            var property = material->MProperties[p];
            if (property->MType != PropertyTypeInfo.String || ReadString(property->MKey) != "?mat.name")
                continue;
            var length = *(int*)property->MData;
            if (length <= 0 || length > property->MDataLength - 4)
                return null;
            return Encoding.UTF8.GetString(property->MData + 4, length);
        }

        return null;
    }

    private static unsafe string ReadString(AssimpString value)
        => Encoding.UTF8.GetString(value.Data, (int)Math.Min(value.Length, 1023));
}
