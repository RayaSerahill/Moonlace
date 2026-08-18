using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Export;
using Moonlace.GameData.Items;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// End-to-end tests of the non-destructive editing pipeline against a real
/// installation. The game directory is only ever read; all session output
/// lives in per-test temp directories. As a hard guarantee, a fixture check
/// asserts the sqpack index files' timestamps are untouched by the run.
/// </summary>
public sealed class EditingSessionTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-edit-tests-" + Guid.NewGuid().ToString("N"));
    private string? _gameDir;
    private DateTime _indexTimestamp;
    private string? _indexFile;

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
        _gameDir = FindGameDir();
        if (_gameDir is null)
            return false;

        _indexFile = Directory.EnumerateFiles(Path.Combine(_gameDir, "sqpack", "ffxiv"), "040000*.index").FirstOrDefault();
        if (_indexFile is not null)
            _indexTimestamp = File.GetLastWriteTimeUtc(_indexFile);

        _service.InitializeAsync(_gameDir).GetAwaiter().GetResult();
        return true;
    }

    public void Dispose()
    {
        // The absolute rule: this test run must not have touched the game files.
        if (_indexFile is not null)
            Assert.Equal(_indexTimestamp, File.GetLastWriteTimeUtc(_indexFile));

        _service.Dispose();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private (SessionService Session, EffectiveAssetProvider Assets, ItemEditingService Editing, RenderModelBuilder Builder, EquipmentItem Item) CreateStack(string itemName = "Dated Bronze Gladius")
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
        return (session, assets, editing, builder, item);
    }

    [SkippableFact]
    public void SessionCopyOnWriteAndEffectiveResolution()
    {
        Skip.IfNot(TryInit());
        var (session, assets, _, _, _) = CreateStack();

        const string path = "chara/weapon/w0201/obj/body/b0001/material/v0005/mt_w0201b0001_a.mtrl";
        var original = assets.TryReadFile(path)!;

        Assert.False(session.IsDirty);
        Assert.False(assets.IsModified(path));

        var modified = (byte[])original.Clone();
        modified[^1] ^= 0xFF;
        session.StoreAsset(path, SessionAssetKind.Material, modified);

        Assert.True(session.IsDirty);
        Assert.True(assets.IsModified(path));
        Assert.Equal(modified, assets.TryReadFile(path));
        Assert.Equal(1, assets.Revision(path));

        // Second store edits the session copy and bumps the revision.
        session.StoreAsset(path, SessionAssetKind.Material, original);
        Assert.Equal(2, assets.Revision(path));

        session.DiscardActiveSession();
        Assert.False(session.IsDirty);
        Assert.Equal(original, assets.TryReadFile(path));
        Assert.Equal(0, assets.Revision(path));
    }

    [SkippableFact]
    public void SessionSurvivesItemSwitchAndReload()
    {
        Skip.IfNot(TryInit());
        var (session, assets, _, _, item) = CreateStack();

        const string path = "chara/weapon/w0201/obj/body/b0001/texture/v11_w0201b0001_d.tex";
        session.StoreAsset(path, SessionAssetKind.Texture, [1, 2, 3]);

        // Navigate away and back: the session must still be there.
        session.ActivateForItem(new EquipmentItem
        {
            RowId = 999999, Name = "Other", Slot = EquipSlot.Body, ModelId = 1, SecondaryId = 0, Variant = 1,
        });
        Assert.False(session.IsDirty);
        Assert.Null(assets.TryReadFile("nonexistent/path"));

        session.ActivateForItem(item);
        Assert.True(session.IsDirty);
        Assert.Equal([1, 2, 3], assets.TryReadFile(path));
    }

    [SkippableFact]
    public async Task GltfRoundTripPreservesGeometryAndRenders()
    {
        Skip.IfNot(TryInit());
        var (session, _, editing, builder, item) = CreateStack();
        Directory.CreateDirectory(_tempRoot);

        var before = await builder.LoadAsync(item);
        var glb = Path.Combine(_tempRoot, "export.glb");
        await editing.ExportModelGltfAsync(item, glb);
        Assert.True(new FileInfo(glb).Length > 1024);

        await editing.ImportModelGltfAsync(item, glb);
        Assert.True(session.IsDirty);
        Assert.Single(session.Entries, e => e.Kind == SessionAssetKind.Model);

        // The session model must render, with matching geometry.
        var after = await builder.LoadAsync(item);
        Assert.Equal(before.Meshes.Count, after.Meshes.Count);
        for (var m = 0; m < before.Meshes.Count; m++)
        {
            Assert.Equal(before.Meshes[m].Indices.Length, after.Meshes[m].Indices.Length);
            var a = before.Meshes[m].Vertices[0].Position;
            var b = after.Meshes[m].Vertices[0].Position;
            Assert.True((a - b).Length() < 1e-3f, $"mesh {m} position drift after GLTF round-trip");
        }

        Assert.True((before.BoundsMax - after.BoundsMax).Length() < 1e-2f, "bounds drift after round-trip");
    }

    [SkippableFact]
    public async Task GltfImportRejectsGarbage()
    {
        Skip.IfNot(TryInit());
        var (session, _, editing, _, item) = CreateStack();
        Directory.CreateDirectory(_tempRoot);

        var bogus = Path.Combine(_tempRoot, "bogus.glb");
        await File.WriteAllTextAsync(bogus, "this is not a gltf");

        await Assert.ThrowsAsync<Interchange.GltfImportException>(() => editing.ImportModelGltfAsync(item, bogus));
        Assert.False(session.IsDirty);
    }

    [SkippableFact]
    public async Task MaterialEditFlowsThroughSessionToRenderer()
    {
        Skip.IfNot(TryInit());
        var (session, _, editing, builder, item) = CreateStack();

        var info = await editing.GetItemInfoAsync(item);
        var material = info.Materials.First(m => m.ColorTable.Length > 0);

        var rows = material.ColorTable.ToArray();
        for (var i = 0; i < rows.Length; i++)
            rows[i].Diffuse = new System.Numerics.Vector3(2f, 0f, 0f); // unmistakably red

        await editing.SetMaterialColorTableAsync(material.GamePath, rows);
        Assert.True(session.IsDirty);

        var info2 = await editing.GetItemInfoAsync(item);
        var edited = info2.Materials.First(m => m.GamePath == material.GamePath);
        Assert.True(edited.Modified);
        Assert.True(Math.Abs(edited.ColorTable[0].Diffuse.X - 2f) < 0.01f);

        // The renderer sees the session material.
        var model = await builder.LoadAsync(item);
        var renderMat = model.Meshes.Select(m => m.Material).First(m => m.GamePath == material.GamePath);
        Assert.True(Math.Abs(renderMat.ColorTable[0].DiffuseColor.X - 2f) < 0.01f);
    }

    [SkippableFact]
    public async Task TextureRoundTripThroughSession()
    {
        Skip.IfNot(TryInit());
        var (session, _, editing, builder, item) = CreateStack();
        Directory.CreateDirectory(_tempRoot);

        var info = await editing.GetItemInfoAsync(item);
        var texture = info.Materials.SelectMany(m => m.Textures).First(t => t.Role == "Diffuse");

        // Export to PNG.
        var png = Path.Combine(_tempRoot, "tex.png");
        await editing.ExportTexturePngAsync(texture.GamePath, png);
        Assert.True(new FileInfo(png).Length > 100);

        // Re-import the exported PNG (same aspect, valid content).
        await editing.ImportTextureAsync(texture.GamePath, png);
        Assert.Contains(session.Entries, e => e.Kind == SessionAssetKind.Texture && e.GamePath == texture.GamePath);

        // Renderer picks up the session texture (dimensions preserved).
        var model = await builder.LoadAsync(item);
        var diffuse = model.Meshes.Select(m => m.Material.Diffuse).First(t => t?.Key == texture.GamePath);
        Assert.NotNull(diffuse);
        Assert.Equal(texture.Width, diffuse.Width);

        // Wrong aspect ratio is rejected with a readable error.
        var wrong = Path.Combine(_tempRoot, "wrong.png");
        await File.WriteAllBytesAsync(wrong, Interchange.ImageIo.EncodePng(10, 3, new byte[10 * 3 * 4]));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => editing.ImportTextureAsync(texture.GamePath, wrong));
        Assert.Contains("aspect ratio", ex.Message);
    }

    [SkippableFact]
    public async Task PmpExportContainsExactlyTheSessionChanges()
    {
        Skip.IfNot(TryInit());
        var (session, _, editing, _, item) = CreateStack();
        Directory.CreateDirectory(_tempRoot);

        // Empty session refuses to export.
        var pmpPath = Path.Combine(_tempRoot, "test.pmp");
        await Assert.ThrowsAsync<PmpExportException>(
            () => editing.ExportPmpAsync(new PmpMetadata { Name = "Test" }, pmpPath));

        // One material change → exactly one packaged file.
        var info = await editing.GetItemInfoAsync(item);
        var material = info.Materials.First(m => m.ColorTable.Length > 0);
        await editing.SetMaterialColorTableAsync(material.GamePath, material.ColorTable);

        await editing.ExportPmpAsync(
            new PmpMetadata { Name = "Moonlace Test", Author = "moonlace", Version = "0.1.0" }, pmpPath);

        using var zip = ZipFile.OpenRead(pmpPath);
        Assert.NotNull(zip.GetEntry("meta.json"));
        Assert.NotNull(zip.GetEntry("default_mod.json"));

        using var metaDoc = JsonDocument.Parse(new StreamReader(zip.GetEntry("meta.json")!.Open()).ReadToEnd());
        Assert.Equal("Moonlace Test", metaDoc.RootElement.GetProperty("Name").GetString());
        Assert.Equal(3, metaDoc.RootElement.GetProperty("FileVersion").GetInt32());

        using var modDoc = JsonDocument.Parse(new StreamReader(zip.GetEntry("default_mod.json")!.Open()).ReadToEnd());
        var files = modDoc.RootElement.GetProperty("Files");
        Assert.Single(files.EnumerateObject());
        var file = files.EnumerateObject().Single();
        Assert.Equal(material.GamePath, file.Name);
        Assert.NotNull(zip.GetEntry(file.Value.GetString()!));

        // Package contains only json + the one changed file.
        Assert.Equal(3, zip.Entries.Count);
    }
}
