using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.App.ViewModels;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Items;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Drives the editor ViewModel commands (the same code paths the tab buttons
/// invoke) against real game data — covering the UI wiring that bare service
/// tests miss. Game data is read-only; sessions live in temp directories.
/// </summary>
public sealed class EditorViewModelIntegrationTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-vmedit-tests-" + Guid.NewGuid().ToString("N"));

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

    private (EditorViewModel Editor, SessionService Session, EquipmentItem Item) CreateEditor(string itemName)
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var editing = new ItemEditingService(assets, resolver, textures, session, link, NullLogger<ItemEditingService>.Instance);
        var editor = new EditorViewModel(editing, session, link, textures, resolver, new NoopPicker(), NullLogger<EditorViewModel>.Instance);

        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = repo.GetEquipmentItemsAsync().GetAwaiter().GetResult();
        return (editor, session, items.First(i => i.Name == itemName));
    }

    [SkippableFact]
    public async Task MeshAssignmentCommandStoresSessionModel()
    {
        Skip.IfNot(TryInit());
        var (editor, session, item) = CreateEditor("Hempen Camise");
        await editor.SetItemAsync(item);

        Assert.Equal(2, editor.MeshAssignments.Count);
        Assert.True(editor.HasMultipleMaterials);
        Assert.NotEqual(editor.MeshAssignments[0].SelectedMaterialIndex, editor.MeshAssignments[1].SelectedMaterialIndex);

        // What the ComboBox binding does, then the Apply button.
        editor.MeshAssignments[1].SelectedMaterialIndex = editor.MeshAssignments[0].SelectedMaterialIndex;
        await editor.ApplyMeshAssignmentsCommand.ExecuteAsync(null);

        Assert.Null(editor.ErrorText);
        Assert.True(session.IsDirty);
        Assert.Contains(session.Entries, e => e.Kind == SessionAssetKind.Model);
        Assert.All(editor.MeshAssignments, m => Assert.Equal(editor.MeshAssignments[0].SelectedMaterialIndex, m.SelectedMaterialIndex));
    }

    [SkippableFact]
    public async Task TextureSlotEditCommandStoresSessionMaterial()
    {
        Skip.IfNot(TryInit());
        var (editor, session, item) = CreateEditor("Dated Bronze Gladius");
        await editor.SetItemAsync(item);

        var material = editor.Materials[0];
        var slot = material.TextureSlots.First(s => s.Role == "Diffuse");
        slot.Path = "chara/equipment/e0003/texture/v13_c0101e0003_top_d.tex";
        await material.ApplyTexturesCommand.ExecuteAsync(null);

        Assert.Null(editor.ErrorText);
        Assert.True(session.IsDirty);

        var refreshed = editor.Materials.First(m => m.GamePath == material.GamePath);
        Assert.Equal("chara/equipment/e0003/texture/v13_c0101e0003_top_d.tex",
            refreshed.TextureSlots.First(s => s.Role == "Diffuse").Path);
        Assert.True(refreshed.Modified);
    }

    [SkippableFact]
    public async Task InvalidTexturePathSurfacesAsErrorWithoutStoring()
    {
        Skip.IfNot(TryInit());
        var (editor, session, item) = CreateEditor("Dated Bronze Gladius");
        await editor.SetItemAsync(item);

        var material = editor.Materials[0];
        material.TextureSlots[0].Path = "chara/nope/missing.tex";
        await material.ApplyTexturesCommand.ExecuteAsync(null);

        Assert.NotNull(editor.ErrorText);
        Assert.Contains("not found", editor.ErrorText);
        Assert.False(session.IsDirty);
    }
}
