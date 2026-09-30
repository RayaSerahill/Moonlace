using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Items;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Adding a diffuse slot to a material that has none, and switching a
/// material's shader pack. Game data is only ever read.
/// </summary>
public sealed class MaterialSlotEditingTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-mtrlslot-tests-" + Guid.NewGuid().ToString("N"));

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

    // A Dawntrail character.shpk material without a diffuse map (mask/id driven).
    private const string DawntrailMtrl = "chara/equipment/e6100/material/v0001/mt_c0101e6100_top_a.mtrl";

    [Theory]
    [InlineData("chara/equipment/e0016/texture/v03_c0201e0016_top_n.tex", "chara/equipment/e0016/texture/v03_c0201e0016_top_d.tex")]
    [InlineData("chara/equipment/e6100/texture/v01_c0101e6100_top_norm.tex", "chara/equipment/e6100/texture/v01_c0101e6100_top_base.tex")]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_b_norm_3393220501.tex", "chara/equipment/e6080/texture/v01_c0201e6080_sho_b_base.tex")]
    public void SuggestedDiffuseSitsNextToTheNormalMap(string normal, string expected)
        => Assert.Equal(expected, MaterialEdits.SuggestDiffusePath([normal], "chara/x/material/v0001/mt_x.mtrl"));

    [Fact]
    public void WithoutANormalMapTheSuggestionFollowsTheMaterialName()
        => Assert.Equal(
            "chara/equipment/e0001/texture/c0101e0001_top_a_d.tex",
            MaterialEdits.SuggestDiffusePath([], "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"));

    [SkippableFact]
    public void AddingADiffuseSlotBindsTheSamplerAndTextureMode()
    {
        Skip.IfNot(TryInit());
        var original = _service.Lumina.GetFile(DawntrailMtrl)?.Data;
        Skip.If(original is null, "test material missing");
        var before = MtrlDocument.Parse(original!);
        Skip.If(MaterialEdits.HasDiffuseSlot(before), "test material already has a diffuse slot");
        Assert.Equal("character.shpk", before.ShaderPack);

        const string diffuse = "chara/equipment/e6100/texture/v01_c0101e6100_top_base.tex";
        var edited = MaterialEdits.AddDiffuseSlot(original!, diffuse);

        var doc = MtrlDocument.Parse(edited);
        var sampler = Assert.Single(doc.Samplers, s => s.SamplerId == MaterialEdits.SamplerDiffuseId);
        Assert.Equal(diffuse, doc.Textures[sampler.TextureIndex].Path);
        Assert.Equal(MaterialEdits.DiffuseSamplerSettings, sampler.Settings);
        Assert.Contains(doc.ShaderKeys, k => k.Category == MaterialEdits.TextureModeKey && k.Value == MaterialEdits.TextureModeWithDiffuse);
        Assert.Single(doc.ShaderKeys, k => k.Category == MaterialEdits.TextureModeKey);

        // Everything else survives, and the existing samplers keep their textures.
        Assert.Equal(before.DataSet, doc.DataSet);
        Assert.Equal(before.ShaderValues, doc.ShaderValues);
        foreach (var old in before.Samplers)
        {
            var kept = Assert.Single(doc.Samplers, s => s.SamplerId == old.SamplerId);
            Assert.Equal(before.Textures[old.TextureIndex].Path, doc.Textures[kept.TextureIndex].Path);
        }

        // Lumina reads the result too.
        var tmp = Path.Combine(_tempRoot, "diffuse.mtrl");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(tmp, edited);
        var lumina = _service.Lumina.GetFileFromDisk<Lumina.Data.Files.MtrlFile>(tmp, DawntrailMtrl);
        Assert.Equal(doc.Textures.Count, lumina.TextureOffsets.Length);

        Assert.Throws<InvalidOperationException>(() => MaterialEdits.AddDiffuseSlot(edited, diffuse));
    }

    [SkippableFact]
    public void ShaderSwitchChangesOnlyTheName()
    {
        Skip.IfNot(TryInit());
        var original = _service.Lumina.GetFile(DawntrailMtrl)?.Data;
        Skip.If(original is null, "test material missing");

        var switched = MaterialEdits.SetShader(original!, "characterlegacy.shpk");
        var before = MtrlDocument.Parse(original!);
        var after = MtrlDocument.Parse(switched);
        Assert.Equal("characterlegacy.shpk", after.ShaderPack);
        Assert.Equal(before.Textures.Select(t => t.Path), after.Textures.Select(t => t.Path));
        Assert.Equal(before.DataSet, after.DataSet);
        Assert.Equal(before.ShaderKeys, after.ShaderKeys);
        Assert.Equal(before.Samplers.Select(s => (s.SamplerId, s.TextureIndex)), after.Samplers.Select(s => (s.SamplerId, s.TextureIndex)));
    }

    // A game-shipped characterlegacy material with a 32-row Dawntrail table and dye block.
    private const string LegacyMtrl = "chara/equipment/e0016/material/v0012/mt_c0201e0016_top_a.mtrl";

    [SkippableFact]
    public void LegacyToCharacterSetsTheRowFieldsThatKeepItFromRenderingBlack()
    {
        Skip.IfNot(TryInit());
        var original = _service.Lumina.GetFile(LegacyMtrl)?.Data;
        Skip.If(original is null, "test material missing");
        var before = MtrlDocument.Parse(original!);
        Assert.Equal("characterlegacy.shpk", before.ShaderPack);
        Assert.True(before.DataSet.Length >= 2048);

        var switched = MaterialEdits.SetShader(original!, "character.shpk");

        var rows = MtrlParser.Parse(switched).ColorTable;
        Assert.Equal(32, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(1f, row.Gloss);            // field 3
            Assert.Equal(0f, row.SpecularStrength); // field 7
            Assert.Equal(1f, row.EmissiveExtra);    // field 11
        });

        // Only those three halfs per row changed; colors and the dye block did not.
        var after = MtrlDocument.Parse(switched);
        Assert.Equal("character.shpk", after.ShaderPack);
        for (var i = 0; i < before.DataSet.Length; i++)
        {
            var inTable = i < 2048;
            var half = inTable ? (i % 64) / 2 : -1;
            if (half is 3 or 7 or 11)
                continue;
            Assert.True(before.DataSet[i] == after.DataSet[i], $"data set byte {i} changed");
        }

        Assert.Equal(before.ShaderKeys, after.ShaderKeys);
    }

    [SkippableFact]
    public void Field11RoundTripsThroughTheColorTableEditor()
    {
        Skip.IfNot(TryInit());
        var original = _service.Lumina.GetFile(LegacyMtrl)?.Data;
        Skip.If(original is null, "test material missing");

        var rows = MtrlParser.Parse(original!).ColorTable.ToArray();
        rows[0].EmissiveExtra = 0.5f;
        rows[5].EmissiveExtra = 1f;
        var patched = MtrlWriter.PatchColorTable(original!, rows);

        var reread = MtrlParser.Parse(patched).ColorTable;
        Assert.Equal(0.5f, reread[0].EmissiveExtra);
        Assert.Equal(1f, reread[5].EmissiveExtra);
        Assert.Equal(rows[1].EmissiveExtra, reread[1].EmissiveExtra);
    }

    [SkippableFact]
    public async Task AddedDiffuseTakesAnImportedImageAndReachesTheViewport()
    {
        Skip.IfNot(TryInit());
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var editing = new ItemEditingService(assets, resolver, textures, session, link, NullLogger<ItemEditingService>.Instance);
        var builder = new RenderModelBuilder(assets, resolver, textures, NullLogger<RenderModelBuilder>.Instance);

        var items = await new ItemRepository(_service, NullLogger<ItemRepository>.Instance).GetEquipmentItemsAsync();
        var item = items.FirstOrDefault(i => i.ModelId == 6100 && i.Slot == EquipSlot.Body && !i.IsAccessory);
        Skip.If(item is null, "no item uses e6100 body");
        session.ActivateForItem(item);
        resolver.PreferredRaceCode = "0101";

        var info = await editing.GetItemInfoAsync(item!);
        var material = info.Materials.FirstOrDefault(m => !m.HasDiffuseSlot && m.ShaderPack == "character.shpk");
        Skip.If(material is null, "no diffuse-less character.shpk material on this item");
        var diffuse = material!.SuggestedDiffusePath;
        Assert.EndsWith("_base.tex", diffuse);

        await editing.AddDiffuseSlotAsync(material.GamePath, diffuse);
        var after = await editing.GetItemInfoAsync(item!);
        var edited = after.Materials.Single(m => m.GamePath == material.GamePath);
        Assert.True(edited.HasDiffuseSlot);
        Assert.True(edited.Modified);
        Assert.Contains(edited.Textures, t => t.GamePath == diffuse);

        // The new path takes an imported image and the viewport binds it as diffuse.
        var png = Path.Combine(_tempRoot, "base.png");
        await File.WriteAllBytesAsync(png, ImageIo.EncodePng(32, 32, Enumerable.Repeat((byte)200, 32 * 32 * 4).ToArray()));
        await editing.ImportTextureAsync(diffuse, png);
        var model = await builder.LoadAsync(item!);
        Assert.Contains(model.Meshes, m => m.Material.Diffuse?.Key == diffuse);

        // Shader switch through the service.
        await editing.SetMaterialShaderAsync(material.GamePath, "characterlegacy.shpk");
        var switched = await editing.GetItemInfoAsync(item!);
        Assert.Equal("characterlegacy.shpk", switched.Materials.Single(m => m.GamePath == material.GamePath).ShaderPack);
        await Assert.ThrowsAsync<ArgumentException>(() => editing.SetMaterialShaderAsync(material.GamePath, "  "));
    }
}
