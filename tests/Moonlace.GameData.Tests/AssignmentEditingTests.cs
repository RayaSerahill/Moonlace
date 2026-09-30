using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Items;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Mesh→material and material→texture reassignment against real game data
/// (read-only; all writes go to temp sessions).
/// </summary>
public sealed class AssignmentEditingTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-assign-tests-" + Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private (SessionService Session, ItemEditingService Editing, RenderModelBuilder Builder, EquipmentItem Item) CreateStack(string itemName)
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var editing = new ItemEditingService(assets, resolver, textures, session, link, NullLogger<ItemEditingService>.Instance);
        var builder = new RenderModelBuilder(assets, resolver, textures, NullLogger<RenderModelBuilder>.Instance);

        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = repo.GetEquipmentItemsAsync().GetAwaiter().GetResult();
        var item = items.First(i => i.Name == itemName);
        session.ActivateForItem(item);
        return (session, editing, builder, item);
    }

    [SkippableFact]
    public async Task MeshMaterialReassignmentFlowsToRenderer()
    {
        Skip.IfNot(TryInit());
        // Hempen Camise: 2 meshes, 2 materials (gear + skin) — swap both meshes onto material 0.
        var (session, editing, builder, item) = CreateStack("Hempen Camise");

        var info = await editing.GetItemInfoAsync(item);
        Assert.True(info.Meshes.Count >= 2, "test needs a multi-mesh model");
        Assert.True(info.MaterialNames.Count >= 2, "test needs a multi-material model");
        Assert.NotEqual(info.Meshes[0].MaterialIndex, info.Meshes[1].MaterialIndex);

        var assignments = info.Meshes.Select(_ => 0).ToArray();
        await editing.SetMeshMaterialsAsync(item, assignments);
        Assert.True(session.IsDirty);

        var after = await editing.GetItemInfoAsync(item);
        Assert.All(after.Meshes, m => Assert.Equal(0, m.MaterialIndex));

        // Renderer picks it up: every mesh now uses the same material path.
        var model = await builder.LoadAsync(item);
        var paths = model.Meshes.Select(m => m.Material.GamePath).Distinct().ToArray();
        Assert.Single(paths);

        // Out-of-range assignment is rejected.
        await Assert.ThrowsAsync<ArgumentException>(
            () => editing.SetMeshMaterialsAsync(item, info.Meshes.Select(_ => 99).ToArray()));
    }

    [SkippableFact]
    public async Task TypedThirdPartyMaterialIsWrittenAsIsAndRendersWhite()
    {
        Skip.IfNot(TryInit());
        var (session, editing, builder, item) = CreateStack("Hempen Camise");
        var info = await editing.GetItemInfoAsync(item);
        var gearMaterial = info.MaterialNames[info.Meshes[0].MaterialIndex];

        // Mesh 0 keeps its material, every other mesh points at a material
        // only some other mod would provide. (Skin-pattern names such as
        // /mt_c0201b0001_bibo.mtrl preview as the vanilla skin instead, see
        // RealGameDataTests.BodyMaterialFallsBackToVanillaSkin.)
        const string custom = "/bibo.mtrl";
        var names = info.Meshes.Select((_, i) => i == 0 ? gearMaterial : custom).ToArray();
        await editing.SetMeshMaterialsAsync(item, names);
        Assert.True(session.IsDirty);

        // The model now lists the typed names verbatim, appended after the originals.
        var after = await editing.GetItemInfoAsync(item);
        Assert.Equal(info.MaterialNames, after.MaterialNames.Take(info.MaterialNames.Count));
        Assert.Equal(custom, after.MaterialNames[^1]);
        Assert.Equal(gearMaterial, after.MaterialNames[after.Meshes[0].MaterialIndex]);
        Assert.Equal(custom, after.MaterialNames[after.Meshes[1].MaterialIndex]);
        // Nobody supplies it, so it is simply not in the editable list.
        Assert.DoesNotContain(after.Materials, m => m.Name == custom);

        // The viewport still loads; the unknown material is the white fallback.
        var model = await builder.LoadAsync(item);
        Assert.Equal("", model.Meshes[1].Material.GamePath);
        Assert.NotEqual("", model.Meshes[0].Material.GamePath);

        // Reusing a typed name does not add it twice.
        await editing.SetMeshMaterialsAsync(item, names);
        var again = await editing.GetItemInfoAsync(item);
        Assert.Equal(after.MaterialNames, again.MaterialNames);

        // Blank names are rejected before anything is written.
        await Assert.ThrowsAsync<ArgumentException>(
            () => editing.SetMeshMaterialsAsync(item, info.Meshes.Select(_ => " ").ToArray()));
    }

    [SkippableFact]
    public void AppendedMaterialNamesAreReadableByLumina()
    {
        Skip.IfNot(TryInit());
        const string path = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var original = MdlParser.Parse(_service.Lumina.GetFile(path)!.Data);
        var names = original.MaterialNames.Append("/bibo.mtrl").ToArray();
        var meshes = original.Meshes
            .Select((m, i) => new ParsedMesh
            {
                Vertices = m.Vertices,
                Indices = m.Indices,
                MaterialIndex = i == 0 ? names.Length - 1 : m.MaterialIndex,
                MaterialName = i == 0 ? "/bibo.mtrl" : m.MaterialName,
                BoneTableIndex = m.BoneTableIndex,
                Submeshes = m.Submeshes,
            })
            .ToArray();

        var written = MdlWriter.Write(original, meshes, original.BoneTables, materialNames: names);
        var reparsed = MdlParser.Parse(written);
        Assert.Equal(names, reparsed.MaterialNames);
        Assert.Equal(original.BoneNames, reparsed.BoneNames);
        Assert.Equal("/bibo.mtrl", reparsed.Meshes[0].MaterialName);

        var tmp = Path.Combine(_tempRoot, "custom-material.mdl");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(tmp, written);
        var lumina = _service.Lumina.GetFileFromDisk<Lumina.Data.Files.MdlFile>(tmp, path);
        Assert.Equal(names.Length, lumina.FileHeader.MaterialCount);
        Assert.Equal(names.Length, lumina.MaterialNameOffsets.Length);
    }

    [SkippableFact]
    public async Task MaterialTextureReassignmentFlowsToRenderer()
    {
        Skip.IfNot(TryInit());
        var (session, editing, builder, item) = CreateStack("Dated Bronze Gladius");

        var info = await editing.GetItemInfoAsync(item);
        var material = info.Materials[0];
        var paths = material.Textures.Select(t => t.GamePath).ToArray();
        var diffuseSlot = Array.FindIndex(paths, p => ItemEditingService.TextureRole(p) == "Diffuse");
        Assert.True(diffuseSlot >= 0);

        // Point the diffuse slot at a different (existing) game texture.
        const string replacement = "chara/equipment/e0003/texture/v13_c0101e0003_top_d.tex";
        Assert.True(_service.Lumina.FileExists(replacement));
        var newPaths = paths.ToArray();
        newPaths[diffuseSlot] = replacement;

        await editing.SetMaterialTexturesAsync(material.GamePath, newPaths);
        Assert.True(session.IsDirty);

        // The rewritten material parses with the new path, same shader, same color table.
        var after = await editing.GetItemInfoAsync(item);
        var edited = after.Materials.First(m => m.GamePath == material.GamePath);
        Assert.Equal(replacement, edited.Textures[diffuseSlot].GamePath);
        Assert.Equal(material.ShaderPack, edited.ShaderPack);
        Assert.Equal(material.ColorTable.Length, edited.ColorTable.Length);
        Assert.Equal(material.ColorTable[2].Diffuse, edited.ColorTable[2].Diffuse);

        // Renderer now samples the replacement texture.
        var model = await builder.LoadAsync(item);
        var diffuse = model.Meshes.Select(m => m.Material).First(m => m.GamePath == material.GamePath).Diffuse;
        Assert.NotNull(diffuse);
        Assert.Equal(replacement, diffuse.Key);

        // A nonexistent path is rejected before anything is stored.
        var bogus = paths.ToArray();
        bogus[diffuseSlot] = "chara/does/not/exist.tex";
        await Assert.ThrowsAsync<InvalidDataException>(
            () => editing.SetMaterialTexturesAsync(material.GamePath, bogus));
    }

    [SkippableFact]
    public void MtrlTextureRewriteSurvivesLuminaReparse()
    {
        Skip.IfNot(TryInit());
        const string mtrlPath = "chara/weapon/w0201/obj/body/b0001/material/v0005/mt_w0201b0001_a.mtrl";
        var original = _service.Lumina.GetFile(mtrlPath)!.Data;
        var parsed = MtrlParser.Parse(original);

        // Longer + shorter replacement paths exercise string-table resizing.
        var newPaths = parsed.TexturePaths.ToArray();
        newPaths[0] = "chara/equipment/e6231/texture/v01_c0101e6231_top_base.tex";

        var rewritten = MtrlWriter.ReplaceTexturePaths(original, newPaths);

        // Independent check via Lumina's own MtrlFile reader.
        var tmp = Path.Combine(Path.GetTempPath(), $"moonlace-test-{Guid.NewGuid():N}.mtrl");
        try
        {
            File.WriteAllBytes(tmp, rewritten);
            var lumina = _service.Lumina.GetFileFromDisk<Lumina.Data.Files.MtrlFile>(tmp, mtrlPath);
            Assert.Equal(parsed.TexturePaths.Count, lumina.TextureOffsets.Length);
            var end = Array.IndexOf(lumina.Strings, (byte)0, lumina.TextureOffsets[0].Offset);
            var firstPath = System.Text.Encoding.UTF8.GetString(
                lumina.Strings, lumina.TextureOffsets[0].Offset, end - lumina.TextureOffsets[0].Offset);
            Assert.Equal(newPaths[0], firstPath);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
    [Theory]
    [InlineData("/mt_c0101e0001_top_a.mtrl", "/mt_c0101e0001_top_d.mtrl")] // _b and _c taken
    [InlineData("/mt_c0101e0001_top_c.mtrl", "/mt_c0101e0001_top_d.mtrl")]
    [InlineData("/mt_c0101e0001_top_x.mtrl", "/mt_c0101e0001_top_y.mtrl")]
    [InlineData("/bibo.mtrl", "/bibo_new.mtrl")]
    public void NewMaterialNamesSkipTakenOnes(string source, string expected)
        => Assert.Equal(expected, ItemEditingService.SuggestMaterialName(
            source, new HashSet<string> { "/mt_c0101e0001_top_a.mtrl", "/mt_c0101e0001_top_b.mtrl", "/mt_c0101e0001_top_c.mtrl" }));

    [Theory]
    [InlineData("mt_x_b", "/mt_x_b.mtrl")]
    [InlineData("/mt_x_b.mtrl", "/mt_x_b.mtrl")]
    [InlineData("  mt_x_b.mtrl ", "/mt_x_b.mtrl")]
    [InlineData("chara/common/x.mtrl", "chara/common/x.mtrl")]
    public void MaterialNamesAreNormalizedToModelStyle(string typed, string expected)
        => Assert.Equal(expected, ItemEditingService.NormalizeMaterialName(typed));

    [SkippableFact]
    public async Task CreatedMaterialIsACopyInTheModelList()
    {
        Skip.IfNot(TryInit());
        var (session, editing, builder, item) = CreateStack("Hempen Camise");
        var info = await editing.GetItemInfoAsync(item);
        var source = info.MaterialNames[info.Meshes[0].MaterialIndex];
        var sourceMaterial = info.Materials.Single(m => m.Name == source);
        var name = ItemEditingService.SuggestMaterialName(source, info.MaterialNames.ToHashSet());

        await editing.CreateMaterialAsync(item, name, source);

        var after = await editing.GetItemInfoAsync(item);
        Assert.Equal(info.MaterialNames.Append(name), after.MaterialNames);
        var created = after.Materials.Single(m => m.Name == name);
        Assert.Equal(sourceMaterial.ShaderPack, created.ShaderPack);
        Assert.Equal(sourceMaterial.Textures.Select(t => t.GamePath), created.Textures.Select(t => t.GamePath));
        Assert.Equal(Path.GetDirectoryName(sourceMaterial.GamePath), Path.GetDirectoryName(created.GamePath));
        // No mesh moved.
        Assert.Equal(info.Meshes.Select(m => m.MaterialIndex), after.Meshes.Select(m => m.MaterialIndex));
        Assert.Contains(session.Entries, e => e.GamePath == created.GamePath && e.Kind == SessionAssetKind.Material);

        await Assert.ThrowsAsync<ArgumentException>(() => editing.CreateMaterialAsync(item, name, source));
        await builder.LoadAsync(item); // still renders
    }

    [SkippableFact]
    public async Task DeletedMaterialLeavesTheListAndItsMeshesMove()
    {
        Skip.IfNot(TryInit());
        var (_, editing, builder, item) = CreateStack("Hempen Camise");
        var info = await editing.GetItemInfoAsync(item);
        Assert.True(info.MaterialNames.Count >= 2, "test needs a multi-material model");
        var removed = info.MaterialNames[0];
        var users = info.Meshes.Count(m => m.MaterialIndex == 0);

        var moved = await editing.DeleteMaterialAsync(item, removed);

        Assert.Equal(users, moved);
        var after = await editing.GetItemInfoAsync(item);
        Assert.Equal(info.MaterialNames.Skip(1), after.MaterialNames);
        Assert.DoesNotContain(after.Materials, m => m.Name == removed);
        // Meshes on the other material still point at it by name.
        for (var i = 0; i < info.Meshes.Count; i++)
        {
            var expected = info.Meshes[i].MaterialIndex == 0 ? after.MaterialNames[0] : info.MaterialNames[info.Meshes[i].MaterialIndex];
            Assert.Equal(expected, after.MaterialNames[after.Meshes[i].MaterialIndex]);
        }

        await builder.LoadAsync(item);

        // The last material cannot go.
        while (after.MaterialNames.Count > 1)
        {
            await editing.DeleteMaterialAsync(item, after.MaterialNames[0]);
            after = await editing.GetItemInfoAsync(item);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => editing.DeleteMaterialAsync(item, after.MaterialNames[0]));
    }

    [SkippableFact]
    public void WriterAcceptsAShortenedMaterialListThatLuminaReads()
    {
        Skip.IfNot(TryInit());
        const string path = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var original = MdlParser.Parse(_service.Lumina.GetFile(path)!.Data);
        Skip.If(original.MaterialNames.Count < 2, "needs two materials");
        var names = original.MaterialNames.Skip(1).ToArray();
        var meshes = original.Meshes.Select(m => new ParsedMesh
        {
            Vertices = m.Vertices,
            Indices = m.Indices,
            MaterialIndex = 0,
            MaterialName = names[0],
            BoneTableIndex = m.BoneTableIndex,
            Submeshes = m.Submeshes,
        }).ToArray();

        var written = MdlWriter.Write(original, meshes, original.BoneTables, materialNames: names);
        var reparsed = MdlParser.Parse(written);
        Assert.Equal(names, reparsed.MaterialNames);
        Assert.All(reparsed.Meshes, m => Assert.Equal(names[0], m.MaterialName));

        var tmp = Path.Combine(_tempRoot, "fewer-materials.mdl");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(tmp, written);
        var lumina = _service.Lumina.GetFileFromDisk<Lumina.Data.Files.MdlFile>(tmp, path);
        Assert.Equal(names.Length, lumina.FileHeader.MaterialCount);
    }
}
