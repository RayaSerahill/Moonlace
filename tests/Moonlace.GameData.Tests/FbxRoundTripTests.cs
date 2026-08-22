using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// FBX export/import round trips. The synthetic test runs everywhere; the
/// real-game-data tests skip when no install is present. Game data is only
/// ever read; written files live in temp directories.
/// </summary>
public sealed class FbxRoundTripTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("moonlace-fbx-test-").FullName;
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    // --- synthetic round trip ---

    private static ParsedModel BuildSyntheticModel()
    {
        // A little skinned wedge: four distinct vertices, two triangles,
        // two bones, unique positions so welded vertices stay identifiable.
        var vertices = new ParsedVertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new ParsedVertex
            {
                Position = new Vector3(0.1f + 0.2f * i, 0.5f + 0.1f * (i % 2), 0.05f * i),
                Normal = Vector3.Normalize(new Vector3(0.1f * i, 1, 0.2f)),
                Uv = new Vector2(0.1f + 0.2f * i, 0.15f + 0.1f * i),
                Tangent = new Vector4(1, 0, 0, i % 2 == 0 ? 1 : -1),
                Color = new Vector4(0.25f * i, 1 - 0.25f * i, 0.5f, 1),
                BlendWeights = new Vector4(0.75f, 0.25f, 0, 0),
                BlendIndicesPacked = 0x0100u, // table slots 0 and 1
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
                },
            ],
            MaterialNames = ["/mt_c0101e0001_top_a.mtrl"],
            BoneNames = ["j_kosi", "j_sebo_a", "j_unused"],
            BoneTables = [[0, 1]],
        };
    }

    [Fact]
    public void SyntheticModelSurvivesFbxRoundTrip()
    {
        var model = BuildSyntheticModel();
        var fbx = TempFile("synthetic.fbx");
        FbxExporter.Export(
            model,
            [new ModelMaterialInfo { Name = model.MaterialNames[0] }],
            fbx);
        Assert.True(new FileInfo(fbx).Length > 0);

        var import = FbxImporter.Import(fbx, model);
        var mesh = Assert.Single(import.Meshes);
        Assert.Equal(0, mesh.MaterialIndex);
        Assert.Equal(model.MaterialNames[0], mesh.MaterialName);
        Assert.Equal(model.Meshes[0].Indices.Length, mesh.Indices.Length);
        Assert.Equal(model.Meshes[0].Vertices.Length, mesh.Vertices.Length);

        var table = import.BoneTables[mesh.BoneTableIndex];
        foreach (var original in model.Meshes[0].Vertices)
        {
            // assimp may reorder vertices; match by (unique) position.
            var index = Array.FindIndex(mesh.Vertices, v => (v.Position - original.Position).Length() < 1e-4f);
            Assert.True(index >= 0, $"no imported vertex near {original.Position}");
            var imported = mesh.Vertices[index];

            Assert.True((imported.Normal - original.Normal).Length() < 1e-3f, "normal drift");
            Assert.True((imported.Uv - original.Uv).Length() < 1e-4f, "uv drift");
            Assert.True((imported.Color - original.Color).Length() < 1e-3f, "color drift");

            // Weights land on the same bones with the same magnitudes.
            var expected = new Dictionary<ushort, float> { [0] = 0.75f, [1] = 0.25f };
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = influence switch
                {
                    0 => imported.BlendWeights.X,
                    1 => imported.BlendWeights.Y,
                    2 => imported.BlendWeights.Z,
                    _ => imported.BlendWeights.W,
                };
                if (weight <= 0)
                    continue;
                var bone = table[imported.BlendIndex(influence)];
                Assert.True(expected.TryGetValue(bone, out var expectedWeight), $"unexpected bone {bone}");
                Assert.True(Math.Abs(weight - expectedWeight) < 1e-3f, $"weight drift on bone {bone}");
            }
        }
    }

    [Fact]
    public void SubmeshPartitionSurvivesFbxRoundTrip()
    {
        var model = BuildSyntheticModel();
        var partitioned = new ParsedModel
        {
            Meshes =
            [
                new ParsedMesh
                {
                    Vertices = model.Meshes[0].Vertices,
                    Indices = model.Meshes[0].Indices,
                    MaterialName = model.Meshes[0].MaterialName,
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
            MaterialNames = model.MaterialNames,
            BoneNames = model.BoneNames,
            BoneTables = model.BoneTables,
        };

        var fbx = TempFile("parts.fbx");
        FbxExporter.Export(partitioned, [new ModelMaterialInfo { Name = model.MaterialNames[0] }], fbx);

        var import = FbxImporter.Import(fbx, partitioned);
        var mesh = Assert.Single(import.Meshes);
        Assert.Equal(partitioned.Meshes[0].Submeshes, mesh.Submeshes);
        Assert.Equal(6, mesh.Indices.Length);
        Assert.Equal(0, mesh.MaterialIndex);
    }

    [Fact]
    public void GarbageFileIsRejectedWithAClearError()
    {
        var bogus = TempFile("bogus.fbx");
        File.WriteAllBytes(bogus, [1, 2, 3, 4, 5]);
        var ex = Assert.Throws<ModelImportException>(() => FbxImporter.Import(bogus, BuildSyntheticModel()));
        Assert.Contains("bogus.fbx", ex.Message);
    }

    [Fact]
    public void UnknownBoneNameIsRejectedWithAClearError()
    {
        var model = BuildSyntheticModel();
        var fbx = TempFile("bones.fbx");
        FbxExporter.Export(model, [new ModelMaterialInfo { Name = model.MaterialNames[0] }], fbx);

        var template = new ParsedModel
        {
            Meshes = model.Meshes,
            MaterialNames = model.MaterialNames,
            BoneNames = ["j_totally_different"],
            BoneTables = [[0]],
        };
        var ex = Assert.Throws<ModelImportException>(() => FbxImporter.Import(fbx, template));
        Assert.Contains("j_kosi", ex.Message);
    }

    // --- real game data ---

    private static string? FindGameDir()
    {
        var env = Environment.GetEnvironmentVariable("MOONLACE_TEST_GAME_DIR");
        if (env is not null && Directory.Exists(Path.Combine(env, "sqpack")))
            return env;
        const string local = "/mnt/games/pelit/installs/ffxiv/game";
        return Directory.Exists(Path.Combine(local, "sqpack")) ? local : null;
    }

    private bool TryInit()
    {
        var dir = FindGameDir();
        if (dir is null)
            return false;
        _service.InitializeAsync(dir).GetAwaiter().GetResult();
        return true;
    }

    private const string WeaponMdl = "chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl";
    private const string BodyMdl = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    [SkippableTheory]
    [InlineData(WeaponMdl)]
    [InlineData(BodyMdl)]
    public void RealModelSurvivesFbxRoundTripIntoMdl(string path)
    {
        Skip.IfNot(TryInit());
        var original = MdlParser.Parse(_service.Lumina.GetFile(path)!.Data);

        var fbx = TempFile("real.fbx");
        FbxExporter.Export(
            original,
            original.MaterialNames.Select(n => new ModelMaterialInfo { Name = n }).ToArray(),
            fbx);

        var import = FbxImporter.Import(fbx, original);
        Assert.Equal(original.Meshes.Count, import.Meshes.Count);

        for (var m = 0; m < original.Meshes.Count; m++)
        {
            var a = original.Meshes[m];
            var b = import.Meshes[m];
            Assert.Equal(a.MaterialIndex, b.MaterialIndex);
            Assert.Equal(a.Indices.Length, b.Indices.Length);
            Assert.Equal(a.Submeshes, b.Submeshes);
            // assimp welds duplicate corners, so counts may shrink but never grow.
            Assert.True(b.Vertices.Length <= a.Vertices.Length, $"mesh {m} gained vertices");

            var boundsA = Bounds(a.Vertices);
            var boundsB = Bounds(b.Vertices);
            Assert.True((boundsA.Min - boundsB.Min).Length() < 1e-3f, $"mesh {m} min bound drift");
            Assert.True((boundsA.Max - boundsB.Max).Length() < 1e-3f, $"mesh {m} max bound drift");

            var table = import.BoneTables[b.BoneTableIndex];
            foreach (var vertex in b.Vertices)
            {
                var sum = vertex.BlendWeights.X + vertex.BlendWeights.Y + vertex.BlendWeights.Z + vertex.BlendWeights.W;
                Assert.True(Math.Abs(sum - 1f) < 1e-3f, $"mesh {m} weights do not sum to 1 ({sum})");
                for (var influence = 0; influence < 4; influence++)
                {
                    if (influence switch
                        {
                            0 => vertex.BlendWeights.X,
                            1 => vertex.BlendWeights.Y,
                            2 => vertex.BlendWeights.Z,
                            _ => vertex.BlendWeights.W,
                        } > 0)
                        Assert.InRange(vertex.BlendIndex(influence), 0, table.Length - 1);
                }
            }
        }

        // The imported result must build a valid .mdl, same as the session flow.
        var written = MdlWriter.Write(original, import.Meshes, import.BoneTables);
        var reparsed = MdlParser.Parse(written);
        Assert.Equal(import.Meshes.Count, reparsed.Meshes.Count);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<ParsedVertex> vertices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        return (min, max);
    }
}
