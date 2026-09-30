using System.Numerics;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Imports rigged to a body kit's own armature: bone names the original
/// model does not use are added to the bone list instead of rejected,
/// cosmetic name differences (armature prefixes, Blender duplicate
/// suffixes) collapse onto the template's bones, and unweighted helper
/// bones never reach the model. Game data is only ever read.
/// </summary>
public sealed class CustomSkeletonImportTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("moonlace-skeleton-test-").FullName;
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

    /// <summary>
    /// A skinned wedge rigged like a body kit export: weights on bones
    /// 0 and 1 of <paramref name="boneNames"/>, the rest unweighted.
    /// </summary>
    private static ParsedModel BuildKitRiggedModel(params string[] boneNames)
    {
        var vertices = new ParsedVertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new ParsedVertex
            {
                Position = new Vector3(0.1f + 0.2f * i, 0.5f + 0.1f * (i % 2), 0.05f * i),
                Normal = Vector3.UnitY,
                Uv = new Vector2(0.1f + 0.2f * i, 0.15f + 0.1f * i),
                Tangent = new Vector4(1, 0, 0, 1),
                Color = Vector4.One,
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
            BoneNames = boneNames,
            BoneTables = [[.. Enumerable.Range(0, boneNames.Length).Select(i => (ushort)i)]],
        };
    }

    /// <summary>The template the kit export lands on: the game model only knows j_kosi.</summary>
    private static ParsedModel GameTemplate(ParsedModel geometry) => new()
    {
        Meshes = geometry.Meshes,
        MaterialNames = geometry.MaterialNames,
        BoneNames = ["j_kosi"],
        BoneTables = [[0]],
    };

    /// <summary>Every vertex carries 0.75 on j_kosi and 0.25 on the kit bone, by name.</summary>
    private static void AssertWeightsLandOn(ModelImportResult import, string kitBone)
    {
        var mesh = Assert.Single(import.Meshes);
        var table = import.BoneTables[mesh.BoneTableIndex];
        foreach (var vertex in mesh.Vertices)
        {
            var byName = new Dictionary<string, float>();
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = influence switch
                {
                    0 => vertex.BlendWeights.X,
                    1 => vertex.BlendWeights.Y,
                    2 => vertex.BlendWeights.Z,
                    _ => vertex.BlendWeights.W,
                };
                if (weight > 0)
                    byName[import.BoneNames[table[vertex.BlendIndex(influence)]]] = weight;
            }

            Assert.Equal(2, byName.Count);
            Assert.True(Math.Abs(byName["j_kosi"] - 0.75f) < 1e-3f, "j_kosi weight drift");
            Assert.True(Math.Abs(byName[kitBone] - 0.25f) < 1e-3f, $"{kitBone} weight drift");
        }
    }

    [Theory]
    [InlineData("j_kosi", "j_kosi")]
    [InlineData("Armature|j_kosi", "j_kosi")]
    [InlineData("Armature:j_kosi", "j_kosi")]
    [InlineData("j_kosi.001", "j_kosi")]
    [InlineData("  j_kosi ", "j_kosi")]
    [InlineData("iv_ko_c_l", "iv_ko_c_l")]
    [InlineData("j_sk_f_a_l.12", "j_sk_f_a_l.12")] // not a Blender suffix: kept
    public void BoneNamesAreCleanedOfArmatureNoise(string raw, string expected)
        => Assert.Equal(expected, BoneNameResolver.Clean(raw));

    [Fact]
    public void ResolverReusesTemplateBonesAndAppendsTheRest()
    {
        var bones = new BoneNameResolver(["j_kosi", "j_sebo_a"]);
        Assert.Equal(0, bones.Resolve("j_kosi"));
        Assert.Equal(1, bones.Resolve("Armature|J_Sebo_A.001"));
        Assert.Equal(2, bones.Resolve("iv_ko_c_l"));
        Assert.Equal(2, bones.Resolve("Armature|iv_ko_c_l"));
        Assert.Equal(["j_kosi", "j_sebo_a", "iv_ko_c_l"], bones.BoneNames);
        Assert.Equal(["iv_ko_c_l"], bones.AddedBones);
    }

    [Fact]
    public void GltfRiggedToAKitSkeletonImports()
    {
        // Prefixed template bone, a kit-only bone, and an unweighted helper.
        var kit = BuildKitRiggedModel("Armature|j_kosi", "iv_ko_c_l", "kit_ik_helper");
        var glb = TempFile("kit.glb");
        GltfExporter.Export(kit, [new ModelMaterialInfo { Name = kit.MaterialNames[0] }], glb);

        var import = GltfImporter.Import(glb, GameTemplate(kit));

        Assert.Equal(["j_kosi", "iv_ko_c_l"], import.BoneNames);
        Assert.Equal(["iv_ko_c_l"], import.AddedBones);
        AssertWeightsLandOn(import, "iv_ko_c_l");
    }

    [Fact]
    public void FbxRiggedToAKitSkeletonImports()
    {
        var kit = BuildKitRiggedModel("j_kosi.001", "iv_ko_c_l", "kit_ik_helper");
        var fbx = TempFile("kit.fbx");
        FbxExporter.Export(kit, [new ModelMaterialInfo { Name = kit.MaterialNames[0] }], fbx);

        var import = FbxImporter.Import(fbx, GameTemplate(kit));

        Assert.Equal(["j_kosi", "iv_ko_c_l"], import.BoneNames);
        Assert.Equal(["iv_ko_c_l"], import.AddedBones);
        AssertWeightsLandOn(import, "iv_ko_c_l");
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

    private const string BodyMdl = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    [SkippableFact]
    public void KitBonesSurviveTheMdlWriter()
    {
        Skip.IfNot(TryInit());
        var original = MdlParser.Parse(_service.Lumina.GetFile(BodyMdl)!.Data);

        // Kit rig: everything the game model had, plus two kit-only bones
        // weighted into the first mesh's table.
        string[] kitBones = ["iv_ko_c_l", "iv_ko_c_r"];
        var boneNames = original.BoneNames.Concat(kitBones).ToArray();
        var tables = original.BoneTables.Select(t => t.ToList()).ToList();
        var table = tables[original.Meshes[0].BoneTableIndex];
        Skip.If(table.Count > 62, "first mesh's bone table has no room for two more bones");
        table.Add((ushort)original.BoneNames.Count);
        table.Add((ushort)(original.BoneNames.Count + 1));

        var written = MdlWriter.Write(original, original.Meshes, tables.Select(t => t.ToArray()).ToArray(), boneNames);
        var reparsed = MdlParser.Parse(written);

        Assert.Equal(boneNames, reparsed.BoneNames);
        Assert.Equal(original.MaterialNames, reparsed.MaterialNames);
        Assert.Equal(original.AttributeNames, reparsed.AttributeNames);
        Assert.Equal(tables[original.Meshes[0].BoneTableIndex], reparsed.BoneTables[original.Meshes[0].BoneTableIndex]);

        // Lumina reads the grown string table independently.
        var tmp = TempFile("kit.mdl");
        File.WriteAllBytes(tmp, written);
        var lumina = _service.Lumina.GetFileFromDisk<MdlFile>(tmp, BodyMdl);
        Assert.Equal(boneNames.Length, lumina.BoneNameOffsets.Length);
        var model = new Lumina.Models.Models.Model(lumina);
        Assert.Contains("iv_ko_c_r", model.StringOffsetToStringMap.Values);
    }

    [SkippableFact]
    public void MismatchedBonePrefixIsRejectedByTheWriter()
    {
        Skip.IfNot(TryInit());
        var original = MdlParser.Parse(_service.Lumina.GetFile(BodyMdl)!.Data);
        var shuffled = original.BoneNames.Reverse().Append("iv_ko_c_l").ToArray();
        Assert.Throws<ArgumentException>(
            () => MdlWriter.Write(original, original.Meshes, original.BoneTables, shuffled));
    }
}
