using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.App.ViewModels;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Items;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Model-version (race variant) selection: enumeration, resolution, and that
/// edits land on the selected version's assets. Real game data, read-only.
/// </summary>
public sealed class ModelVariantTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-variant-tests-" + Guid.NewGuid().ToString("N"));

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

    private sealed class NoopPicker : Moonlace.App.Services.IFilePickerService
    {
        public Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, IReadOnlyList<string> patterns)
            => Task.FromResult<string?>(null);
    }

    private (AssetPathResolver Resolver, EditorViewModel Editor, SessionService Session, RenderModelBuilder Builder, IReadOnlyList<EquipmentItem> Items) CreateStack()
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var editing = new ItemEditingService(assets, resolver, textures, session, link, NullLogger<ItemEditingService>.Instance);
        var builder = new RenderModelBuilder(assets, resolver, textures, NullLogger<RenderModelBuilder>.Instance);
        var editor = new EditorViewModel(editing, session, link, textures, resolver, new NoopPicker(), NullLogger<EditorViewModel>.Instance);

        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = repo.GetEquipmentItemsAsync().GetAwaiter().GetResult();
        return (resolver, editor, session, builder, items);
    }

    [SkippableFact]
    public void VariantEnumerationAndPreferredResolution()
    {
        Skip.IfNot(TryInit());
        var (resolver, _, _, _, items) = CreateStack();

        // Hempen Camise has race-specific models; find one with a Miqo'te ♀ variant.
        var item = items.First(i => i.Name == "Hempen Camise");
        var variants = resolver.GetAvailableVariants(item);
        Assert.True(variants.Count > 1, "expected multiple race variants");
        Assert.Contains(variants, v => v.Code == "0101");

        var alt = variants.First(v => v.Code != "0101");
        resolver.PreferredRaceCode = alt.Code;
        var resolved = resolver.Resolve(item);
        Assert.Contains($"c{alt.Code}", resolved.MdlPath);

        resolver.PreferredRaceCode = null;
        Assert.Contains("c0101", resolver.Resolve(item).MdlPath);

        // A code with no model for this item falls back instead of failing.
        resolver.PreferredRaceCode = "9901";
        Assert.Contains("c0101", resolver.Resolve(item).MdlPath);

        // Weapons have no race variants.
        var weapon = items.First(i => i.Slot == EquipSlot.MainHand);
        Assert.Empty(resolver.GetAvailableVariants(weapon));
    }

    [SkippableFact]
    public async Task EditsApplyToTheSelectedVersionOnly()
    {
        Skip.IfNot(TryInit());
        var (resolver, editor, session, builder, items) = CreateStack();
        var item = items.First(i => i.Name == "Hempen Camise");

        await editor.SetItemAsync(item);
        Assert.True(editor.HasMultipleVersions);
        Assert.Equal("0101", editor.SelectedVersion?.Code);

        // Switch to the Miqo'te ♀ variant when present (else any non-default).
        var target = editor.ModelVersions.FirstOrDefault(v => v.Code == "0801")
                     ?? editor.ModelVersions.First(v => v.Code != "0101");
        editor.SelectedVersion = target;
        await WaitUntil(() => editor.ModelPath.Contains($"c{target.Code}"));

        // The tabs now describe the selected version's assets.
        Assert.Contains($"c{target.Code}", editor.ModelPath);

        // An edit lands on that version's model path — and only there.
        editor.MeshAssignments[1].SelectedMaterialIndex = editor.MeshAssignments[0].SelectedMaterialIndex;
        await editor.ApplyMeshAssignmentsCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorText);

        var entry = Assert.Single(session.Entries, e => e.Kind == SessionAssetKind.Model);
        Assert.Contains($"c{target.Code}", entry.GamePath);
        Assert.DoesNotContain("c0101", entry.GamePath);

        // Viewport for the selected version reflects the edit...
        var edited = await builder.LoadAsync(item);
        Assert.Single(edited.Meshes.Select(m => m.Material.GamePath).Distinct());

        // ...while the default version stays untouched.
        resolver.PreferredRaceCode = "0101";
        var original = await builder.LoadAsync(item);
        Assert.True(original.Meshes.Select(m => m.Material.GamePath).Distinct().Count() > 1,
            "the c0101 model must not inherit the c-variant edit");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not reached in time");
    }
}
