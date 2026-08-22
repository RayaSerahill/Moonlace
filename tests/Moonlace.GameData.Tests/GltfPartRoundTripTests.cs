using System.Numerics;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// GLTF export/import round trips for the submesh part convention. Purely
/// synthetic; runs without a game installation.
/// </summary>
public sealed class GltfPartRoundTripTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("moonlace-gltf-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ParsedModel BuildPartitionedModel()
    {
        var vertices = new ParsedVertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new ParsedVertex
            {
                Position = new Vector3(0.1f + 0.2f * i, 0.5f + 0.1f * (i % 2), 0.05f * i),
                Normal = Vector3.Normalize(new Vector3(0.1f * i, 1, 0.2f)),
                Uv = new Vector2(0.1f + 0.2f * i, 0.15f + 0.1f * i),
                Tangent = new Vector4(1, 0, 0, 1),
                Color = new Vector4(0.25f * i, 1 - 0.25f * i, 0.5f, 1),
                BlendWeights = new Vector4(1, 0, 0, 0),
                BlendIndicesPacked = 0,
            };
        }

        return new ParsedModel
        {
            Meshes =
            [
                new ParsedMesh
                {
                    Vertices = vertices,
                    Indices = [0, 1, 2, 1, 3, 2],
                    MaterialName = "/mt_c0101e0001_top_a.mtrl",
                    MaterialIndex = 0,
                    BoneTableIndex = 0,
                    // One triangle per part, with distinct attribute masks
                    // and bone map slices to verify restoration.
                    Submeshes =
                    [
                        new ParsedSubmesh(0, 3, AttributeMask: 1, BoneStartIndex: 5, BoneCount: 2),
                        new ParsedSubmesh(3, 3, AttributeMask: 2, BoneStartIndex: 7, BoneCount: 1),
                    ],
                },
            ],
            MaterialNames = ["/mt_c0101e0001_top_a.mtrl"],
            BoneNames = ["j_kosi"],
            BoneTables = [[0]],
        };
    }

    [Fact]
    public void SubmeshPartitionSurvivesGltfRoundTrip()
    {
        var model = BuildPartitionedModel();
        var glb = Path.Combine(_tempDir, "parts.glb");
        GltfExporter.Export(model, [new ModelMaterialInfo { Name = model.MaterialNames[0] }], glb);

        var import = GltfImporter.Import(glb, model);
        var mesh = Assert.Single(import.Meshes);
        Assert.Equal(model.Meshes[0].Submeshes, mesh.Submeshes);
        Assert.Equal(6, mesh.Indices.Length);
        Assert.Equal(0, mesh.MaterialIndex);
        Assert.Equal(model.MaterialNames[0], mesh.MaterialName);
    }

    [Fact]
    public void PartsWithDifferentMaterialsAreRejected()
    {
        var model = BuildPartitionedModel();
        var twoMaterials = new ParsedModel
        {
            Meshes = model.Meshes,
            MaterialNames = [model.MaterialNames[0], "/mt_c0101e0001_top_b.mtrl"],
            BoneNames = model.BoneNames,
            BoneTables = model.BoneTables,
        };

        var glb = Path.Combine(_tempDir, "mixed.glb");
        GltfExporter.Export(model, [new ModelMaterialInfo { Name = model.MaterialNames[0] }], glb);

        // Give one part its own, differently named material so the parts disagree.
        var gltf = SharpGLTF.Schema2.ModelRoot.Load(glb);
        var secondPart = gltf.LogicalMeshes.First(m => m.Name == "mesh_0.1");
        var other = gltf.CreateMaterial("/mt_c0101e0001_top_b.mtrl");
        secondPart.Primitives[0].Material = other;
        var mixed = Path.Combine(_tempDir, "mixed2.glb");
        gltf.SaveGLB(mixed);

        var ex = Assert.Throws<ModelImportException>(() => GltfImporter.Import(mixed, twoMaterials));
        Assert.Contains("different materials", ex.Message);
    }
}
