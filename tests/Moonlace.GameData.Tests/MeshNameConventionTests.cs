using System.Numerics;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// The TexTools/Penumbra mesh naming convention: only the trailing numbers
/// of a mesh name decide which FFXIV mesh and part it belongs to. Purely
/// synthetic; runs without a game installation.
/// </summary>
public sealed class MeshNameConventionTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("moonlace-meshname-test-").FullName;

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

    [Theory]
    [InlineData("chest 0.0", 0, 0)]
    [InlineData("foot 0.6", 0, 6)]
    [InlineData("mesh_2.1", 2, 1)]
    [InlineData("mesh_2.1.001", 2, 1)] // Blender duplicate suffix
    [InlineData("chest 0.001", 0, 1)] // TexTools reads this as part 1
    [InlineData("Group 3", 3, 0)] // bare number = part 0
    [InlineData("mesh_2", 2, 0)]
    [InlineData("Part 1-4", 1, 4)] // TexTools also allows '-'
    [InlineData("2.5", 2, 5)]
    [InlineData("fancy hat^1.2", 1, 2)]
    public void TrailingNumbersParse(string name, int mesh, int part)
    {
        Assert.True(ModelImportShared.TryParsePartName(name, out var meshIndex, out var partIndex));
        Assert.Equal(mesh, meshIndex);
        Assert.Equal(part, partIndex);
    }

    [Theory]
    [InlineData("Cube")]
    [InlineData("Cube.001")] // Blender default duplicate, no part convention
    [InlineData("hat_h0249")] // digits not preceded by a separator
    [InlineData("j_kosi")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherNamesDoNotParse(string? name)
    {
        Assert.False(ModelImportShared.TryParsePartName(name, out _, out _));
    }

    private static ParsedMesh BuildMesh(
        uint firstMask, uint secondMask, string materialName, int materialIndex = 0, bool partitioned = true)
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

        return new ParsedMesh
        {
            Vertices = vertices,
            Indices = [0, 1, 2, 1, 3, 2],
            MaterialIndex = materialIndex,
            MaterialName = materialName,
            BoneTableIndex = 0,
            Submeshes = partitioned
                ?
                [
                    new ParsedSubmesh(0, 3, AttributeMask: firstMask, BoneStartIndex: 5, BoneCount: 2),
                    new ParsedSubmesh(3, 3, AttributeMask: secondMask, BoneStartIndex: 7, BoneCount: 1),
                ]
                : [],
        };
    }

    [Fact]
    public void RenamedMeshesStillRegroupByTrailingNumbers()
    {
        var model = new ParsedModel
        {
            Meshes = [BuildMesh(1, 2, "/mt_c0101e0001_top_a.mtrl")],
            MaterialNames = ["/mt_c0101e0001_top_a.mtrl"],
            BoneNames = ["j_kosi"],
            BoneTables = [[0]],
        };

        var glb = Path.Combine(_tempDir, "renamed.glb");
        GltfExporter.Export(model, [new ModelMaterialInfo { Name = model.MaterialNames[0] }], glb);

        // A user renamed the parts in Blender; only the trailing numbers remain meaningful.
        var gltf = SharpGLTF.Schema2.ModelRoot.Load(glb);
        gltf.LogicalMeshes.First(m => m.Name == "mesh_0.0").Name = "cute chest 0.0";
        gltf.LogicalMeshes.First(m => m.Name == "mesh_0.1").Name = "cute chest 0.1";
        var renamed = Path.Combine(_tempDir, "renamed2.glb");
        gltf.SaveGLB(renamed);

        var import = GltfImporter.Import(renamed, model);
        var merged = Assert.Single(import.Meshes);
        Assert.Equal(model.Meshes[0].Submeshes, merged.Submeshes);
        Assert.Equal(model.MaterialNames[0], merged.MaterialName);
    }

    [Fact]
    public void MeshNumberPicksTemplateMeshAndItsMaterial()
    {
        // Template with two meshes on different materials; the import brings
        // only mesh 1, renamed and with an unrecognized material name.
        var partitioned = BuildMesh(4, 8, "/mt_c0101e0001_top_b.mtrl", materialIndex: 1);
        var plain = BuildMesh(1, 2, "/mt_c0101e0001_top_a.mtrl", partitioned: false);
        var template = new ParsedModel
        {
            Meshes = [plain, partitioned],
            MaterialNames = ["/mt_c0101e0001_top_a.mtrl", "/mt_c0101e0001_top_b.mtrl"],
            BoneNames = ["j_kosi"],
            BoneTables = [[0]],
        };

        // Export a single-mesh model whose file-side mesh index is 0.
        var exported = new ParsedModel
        {
            Meshes = [BuildMesh(4, 8, "/mt_c0101e0001_top_b.mtrl")],
            MaterialNames = ["/mt_c0101e0001_top_b.mtrl"],
            BoneNames = ["j_kosi"],
            BoneTables = [[0]],
        };
        var glb = Path.Combine(_tempDir, "meshone.glb");
        GltfExporter.Export(exported, [new ModelMaterialInfo { Name = exported.MaterialNames[0] }], glb);

        var gltf = SharpGLTF.Schema2.ModelRoot.Load(glb);
        gltf.LogicalMeshes.First(m => m.Name == "mesh_0.0").Name = "leg 1.0";
        gltf.LogicalMeshes.First(m => m.Name == "mesh_0.1").Name = "leg 1.1";
        foreach (var material in gltf.LogicalMaterials)
            material.Name = "Material.002"; // nothing the template knows
        var renamed = Path.Combine(_tempDir, "meshone2.glb");
        gltf.SaveGLB(renamed);

        var import = GltfImporter.Import(renamed, template);
        var merged = Assert.Single(import.Meshes);
        Assert.Equal(1, merged.MaterialIndex);
        Assert.Equal("/mt_c0101e0001_top_b.mtrl", merged.MaterialName);
        Assert.Equal(template.Meshes[1].Submeshes, merged.Submeshes);
    }
}
