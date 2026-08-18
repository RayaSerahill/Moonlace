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
        var resolver = new AssetPathResolver(_service, NullLogger<AssetPathResolver>.Instance);
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
}
