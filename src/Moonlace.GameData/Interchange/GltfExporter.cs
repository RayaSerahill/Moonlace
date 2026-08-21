using System.Numerics;
using Moonlace.GameData.Parsing;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Moonlace.GameData.Interchange;

/// <summary>
/// Exports the effective model as a binary GLTF (.glb) aimed at Blender
/// interop: geometry, normals, tangents, UVs, vertex colors, skin weights
/// with named joints, material slots named after the FFXIV materials, and
/// embedded base-color/normal textures where available.
///
/// The skeleton is a flat list of named joints (FFXIV bone hierarchies live
/// in .sklb files outside this version's scope); that is enough for Blender
/// to build vertex groups by bone name and preserve them on re-export.
/// </summary>
public static class GltfExporter
{
    public static void Export(ParsedModel model, IReadOnlyList<ModelMaterialInfo> materials, string outputPath)
    {
        var scene = new SceneBuilder("moonlace");

        var materialBuilders = materials
            .Select(m =>
            {
                var mb = new MaterialBuilder(m.Name).WithMetallicRoughnessShader().WithDoubleSide(true);
                if (m.BaseColorPng is not null)
                    mb.WithChannelImage(KnownChannel.BaseColor, new SharpGLTF.Memory.MemoryImage(m.BaseColorPng));
                if (m.NormalPng is not null)
                    mb.WithChannelImage(KnownChannel.Normal, new SharpGLTF.Memory.MemoryImage(m.NormalPng));
                return mb;
            })
            .ToArray();
        var fallbackMaterial = new MaterialBuilder("moonlace_fallback").WithMetallicRoughnessShader();

        // Flat skeleton: named joints under one root so weights survive Blender.
        var root = new NodeBuilder("n_root");
        var joints = model.BoneNames.Select(name => root.CreateNode(name)).ToArray();

        var meshIndex = 0;
        foreach (var mesh in model.Meshes)
        {
            var boneTable = mesh.BoneTableIndex < model.BoneTables.Count
                ? model.BoneTables[mesh.BoneTableIndex]
                : [];

            var material = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < materialBuilders.Length
                ? materialBuilders[mesh.MaterialIndex]
                : fallbackMaterial;

            var builder = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4>(
                $"mesh_{meshIndex}");
            var primitive = builder.UsePrimitive(material);

            var vertices = mesh.Vertices
                .Select(v => BuildVertex(v, boneTable, model.BoneNames.Count))
                .ToArray();

            for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                primitive.AddTriangle(
                    vertices[mesh.Indices[i]],
                    vertices[mesh.Indices[i + 1]],
                    vertices[mesh.Indices[i + 2]]);
            }

            if (joints.Length > 0)
                scene.AddSkinnedMesh(builder, Matrix4x4.Identity, joints);
            else
                scene.AddRigidMesh(builder, Matrix4x4.Identity);
            meshIndex++;
        }

        var gltf = scene.ToGltf2();
        gltf.SaveGLB(outputPath);
    }

    private static VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4> BuildVertex(
        ParsedVertex v, ushort[] boneTable, int boneCount)
    {
        var normal = v.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(v.Normal) : Vector3.UnitY;
        var tangentXyz = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
        // GLTF requires unit tangents with w = ±1; synthesize one when the model has none.
        Vector4 tangent;
        if (tangentXyz.LengthSquared() > 1e-6f)
        {
            tangentXyz = Vector3.Normalize(tangentXyz - normal * Vector3.Dot(normal, tangentXyz));
            if (tangentXyz.LengthSquared() < 1e-6f)
                tangentXyz = OrthogonalTo(normal);
            tangent = new Vector4(tangentXyz, v.Tangent.W >= 0 ? 1f : -1f);
        }
        else
        {
            tangent = new Vector4(OrthogonalTo(normal), 1f);
        }

        var geometry = new VertexPositionNormalTangent(v.Position, normal, tangent);
        var attributes = new VertexColor1Texture1(v.Color, v.Uv);

        var bindings = new List<(int JointIndex, float Weight)>(4);
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = influence switch
            {
                0 => v.BlendWeights.X,
                1 => v.BlendWeights.Y,
                2 => v.BlendWeights.Z,
                _ => v.BlendWeights.W,
            };
            if (weight <= 0)
                continue;

            int tableIndex = v.BlendIndex(influence);
            var joint = tableIndex < boneTable.Length ? boneTable[tableIndex] : 0;
            if (joint < boneCount)
                bindings.Add((joint, weight));
        }

        if (bindings.Count == 0)
            bindings.Add((0, 1f));

        return new VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4>(
            geometry, attributes, new VertexJoints4([.. bindings]));
    }

    private static Vector3 OrthogonalTo(Vector3 normal)
    {
        var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(reference, normal));
    }
}
