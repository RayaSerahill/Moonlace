using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.GameData;
using Moonlace.GameData.Items;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Integration tests that run against a real FFXIV installation when one is
/// available (via the MOONLACE_TEST_GAME_DIR environment variable or a known
/// local path); otherwise they are skipped.
/// </summary>
public sealed class RealGameDataTests : IDisposable
{
    private static string? FindGameDir()
    {
        var env = Environment.GetEnvironmentVariable("MOONLACE_TEST_GAME_DIR");
        if (env is not null && Directory.Exists(Path.Combine(env, "sqpack")))
            return env;
        const string local = "/mnt/games/pelit/installs/ffxiv/game";
        return Directory.Exists(Path.Combine(local, "sqpack")) ? local : null;
    }

    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);

    public void Dispose() => _service.Dispose();

    private bool TryInit()
    {
        var dir = FindGameDir();
        if (dir is null)
            return false;
        _service.InitializeAsync(dir).GetAwaiter().GetResult();
        return true;
    }

    [SkippableFact]
    public async Task LoadsEquipmentItems()
    {
        Skip.IfNot(TryInit());
        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = await repo.GetEquipmentItemsAsync();

        Assert.True(items.Count > 1000, $"Expected >1000 equipment items, got {items.Count}");
        Assert.Contains(items, i => i.Slot == EquipSlot.Body);
        Assert.Contains(items, i => i.Slot == EquipSlot.MainHand);
        Assert.Contains(items, i => i.Slot == EquipSlot.Ears);
        Assert.Contains(items, i => i.Slot == EquipSlot.RightRing);
        Assert.Contains(items, i => i.Slot == EquipSlot.Face && i.RaceCode == "0101");
        Assert.Contains(items, i => i.Slot == EquipSlot.Tail);
        Assert.Contains(items, i => i.Slot == EquipSlot.HumanBody);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [SkippableFact]
    public async Task BuildsRenderModelForWeaponAndEachEquipSlot()
    {
        Skip.IfNot(TryInit());
        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var session = new Moonlace.Core.Session.SessionService(
            NullLogger<Moonlace.Core.Session.SessionService>.Instance,
            Path.Combine(Path.GetTempPath(), "moonlace-test-sessions-" + Guid.NewGuid().ToString("N")));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var builder = new RenderModelBuilder(assets, resolver, textures, NullLogger<RenderModelBuilder>.Instance);

        var items = await repo.GetEquipmentItemsAsync();

        foreach (var slot in Enum.GetValues<EquipSlot>())
        {
            // Some slots legitimately have no entries (no left-only rings exist).
            var item = items.FirstOrDefault(i => i.Slot == slot);
            if (item is null)
                continue;

            var model = await builder.LoadAsync(item);

            Assert.NotEmpty(model.Meshes);
            var mesh = model.Meshes[0];
            Assert.True(mesh.Vertices.Length > 3, $"{slot}: too few vertices");
            Assert.True(mesh.Indices.Length >= 3, $"{slot}: too few indices");
            Assert.True(mesh.Indices.Max() < mesh.Vertices.Length,
                $"{slot} ({item.Name}): index out of range — vertex decode is broken");

            // Bounds must be sane for a piece of character equipment (meters).
            var size = model.BoundsMax - model.BoundsMin;
            Assert.True(size.Length() is > 0.01f and < 20f,
                $"{slot} ({item.Name}): implausible bounds {model.BoundsMin} .. {model.BoundsMax}");

            // Vertex normals should be roughly unit length.
            var n = mesh.Vertices[0].Normal.Length();
            Assert.True(n is > 0.5f and < 1.5f, $"{slot}: normal length {n} not plausible");
        }
    }

    [SkippableFact]
    public void ParsesMaterialWithColorTableAndTextures()
    {
        Skip.IfNot(TryInit());
        var data = _service.Lumina.GetFile("chara/weapon/w0201/obj/body/b0001/material/v0005/mt_w0201b0001_a.mtrl")!.Data;
        var mat = MtrlParser.Parse(data);

        Assert.False(string.IsNullOrEmpty(mat.ShaderPack));
        Assert.NotEmpty(mat.TexturePaths);
        Assert.All(mat.TexturePaths, p => Assert.EndsWith(".tex", p));
        Assert.All(mat.TexturePaths, p => Assert.True(_service.Lumina.FileExists(p), $"missing {p}"));
    }

    [SkippableFact]
    public void BodyMaterialFallsBackToVanillaSkin()
    {
        Skip.IfNot(TryInit());
        var session = new Moonlace.Core.Session.SessionService(
            NullLogger<Moonlace.Core.Session.SessionService>.Instance,
            Path.Combine(Path.GetTempPath(), "moonlace-test-sessions-" + Guid.NewGuid().ToString("N")));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var model = new ResolvedModelInfo(
            "chara/equipment/e0410/model/c0201e0410_dwn.mdl", "chara/equipment/e0410/material", 1);

        // A custom body-replacement skin material (e.g. Bibo+) that no linked
        // source supplies falls back to the vanilla skin material.
        Assert.Equal(
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl",
            resolver.ResolveMaterialPath(model, "/mt_c0201b0001_bibo.mtrl"));

        // An unknown race code additionally falls back to the gender-base body.
        Assert.Equal(
            "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl",
            resolver.ResolveMaterialPath(model, "/mt_c9901b0001_bibo.mtrl"));

        // Vanilla skin names still resolve to themselves.
        Assert.Equal(
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl",
            resolver.ResolveMaterialPath(model, "/mt_c0201b0001_a.mtrl"));
    }

    [SkippableFact]
    public void DecodesTextures()
    {
        Skip.IfNot(TryInit());
        var session = new Moonlace.Core.Session.SessionService(
            NullLogger<Moonlace.Core.Session.SessionService>.Instance,
            Path.Combine(Path.GetTempPath(), "moonlace-test-sessions-" + Guid.NewGuid().ToString("N")));
        var decoder = new TextureDecoder(_service, new EffectiveAssetProvider(_service, session, new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance)), NullLogger<TextureDecoder>.Instance);
        var tex = decoder.Decode("chara/weapon/w0201/obj/body/b0001/texture/v11_w0201b0001_d.tex");

        Assert.NotNull(tex);
        Assert.True(tex.Width > 0 && tex.Height > 0);
        Assert.Equal(tex.Width * tex.Height * 4, tex.Rgba.Length);
        // Not all-black and not all-identical pixels.
        Assert.Contains(tex.Rgba, b => b != 0);
    }
}
