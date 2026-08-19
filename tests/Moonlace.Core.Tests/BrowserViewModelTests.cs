using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.App.ViewModels;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;

namespace Moonlace.Core.Tests;

public sealed class BrowserViewModelTests
{
    private static EquipmentItem Item(uint id, string name, EquipSlot slot = EquipSlot.Body) => new()
    {
        RowId = id,
        Name = name,
        Slot = slot,
        ModelId = 1,
        SecondaryId = 0,
        Variant = 1,
    };

    private sealed class FakeRepository(IReadOnlyList<EquipmentItem> items) : IItemRepository
    {
        public Task<IReadOnlyList<EquipmentItem>> GetEquipmentItemsAsync(CancellationToken ct = default)
            => Task.FromResult(items);
    }

    private sealed class FakeLoader : IRenderModelLoader
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public List<uint> Loaded { get; } = [];

        public async Task<RenderModel> LoadAsync(EquipmentItem item, CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            Loaded.Add(item.RowId);
            return new RenderModel
            {
                Meshes =
                [
                    new RenderMesh
                    {
                        Vertices = new RenderVertex[3],
                        Indices = [0, 1, 2],
                        Material = new RenderMaterial(),
                    },
                ],
                BoundsMin = new System.Numerics.Vector3(-1),
                BoundsMax = new System.Numerics.Vector3(1),
            };
        }
    }

    private sealed class FakePicker : Moonlace.App.Services.IFilePickerService
    {
        public Task<string?> OpenFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, IReadOnlyList<string> patterns)
            => Task.FromResult<string?>(null);
    }

    private static BrowserViewModel Create(IReadOnlyList<EquipmentItem> items, FakeLoader? loader = null)
    {
        // The editor stack is built on uninitialized game-data services; every
        // path that would touch them surfaces as a caught, displayed error.
        var session = new Moonlace.Core.Session.SessionService(
            NullLogger<Moonlace.Core.Session.SessionService>.Instance,
            Path.Combine(Path.GetTempPath(), "moonlace-vmtest-" + Guid.NewGuid().ToString("N")));
        var lumina = new Moonlace.GameData.LuminaGameDataService(
            NullLogger<Moonlace.GameData.LuminaGameDataService>.Instance);
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new Moonlace.GameData.EffectiveAssetProvider(lumina, session, link);
        var resolver = new Moonlace.GameData.Resolution.AssetPathResolver(
            lumina, assets, NullLogger<Moonlace.GameData.Resolution.AssetPathResolver>.Instance);
        var textures = new Moonlace.GameData.TextureDecoder(
            lumina, assets, NullLogger<Moonlace.GameData.TextureDecoder>.Instance);
        var editing = new Moonlace.GameData.Editing.ItemEditingService(
            assets, resolver, textures, session, link, NullLogger<Moonlace.GameData.Editing.ItemEditingService>.Instance);
        var editor = new EditorViewModel(editing, session, link, textures, resolver, new FakePicker(),
            NullLogger<EditorViewModel>.Instance);
        return new BrowserViewModel(new FakeRepository(items), loader ?? new FakeLoader(), editor,
            NullLogger<BrowserViewModel>.Instance);
    }

    private static ItemNode NodeFor(BrowserViewModel vm, string name) =>
        vm.VisibleNodes.OfType<ItemNode>().Single(n => n.Name == name);

    private static IReadOnlyList<EquipmentItem> MixedItems() =>
    [
        Item(1, "Hempen Camise", EquipSlot.Body),
        Item(2, "Cotton Tabard", EquipSlot.Body),
        Item(3, "Leather Boots", EquipSlot.Feet),
        Item(4, "Bronze Ring", EquipSlot.RightRing),
        new EquipmentItem
        {
            RowId = 0x40000001, Name = "Midlander ♂ Face 1", Slot = EquipSlot.Face,
            ModelId = 1, SecondaryId = 0, Variant = 1, RaceCode = "0101",
        },
    ];

    [Fact]
    public async Task CategoriesAreCollapsedByDefaultAndCounted()
    {
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        // Only the main category headers show: Gear, Accessories, Body.
        var mains = vm.VisibleNodes.OfType<CategoryNode>().ToArray();
        Assert.Equal(vm.VisibleNodes.Count, mains.Length);
        Assert.Equal(["Gear", "Accessories", "Body"], mains.Select(c => c.Label));
        Assert.All(mains, c => Assert.False(c.IsExpanded));
        Assert.Equal(3, mains[0].TotalItems);
        Assert.Equal(1, mains[1].TotalItems);
        Assert.Equal(1, mains[2].TotalItems);
    }

    [Fact]
    public async Task TogglingCategoriesExpandsAndCollapsesWithoutChangingTheItemSelection()
    {
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        // Toggling a main category opens it: its subcategories appear
        // (collapsed), no items yet.
        var gear = vm.VisibleNodes.OfType<CategoryNode>().First(c => c.Label == "Gear");
        vm.ToggleCategoryCommand.Execute(gear);
        Assert.True(gear.IsExpanded);
        Assert.Null(vm.SelectedItem);
        var subs = vm.VisibleNodes.OfType<CategoryNode>().Where(c => c.Level == 1).ToArray();
        Assert.Equal(["Body", "Feet"], subs.Select(s => s.Label));
        Assert.Empty(vm.VisibleNodes.OfType<ItemNode>());

        // Expanding a subcategory reveals its items; empty slots have no header.
        vm.ToggleCategoryCommand.Execute(subs.First(s => s.Label == "Feet"));
        Assert.Equal(["Leather Boots"], vm.VisibleNodes.OfType<ItemNode>().Select(n => n.Name));

        // Collapsing the main hides the open subcategory but keeps its state.
        vm.ToggleCategoryCommand.Execute(gear);
        Assert.False(gear.IsExpanded);
        Assert.Equal(3, vm.VisibleNodes.Count);
        vm.ToggleCategoryCommand.Execute(gear);
        Assert.Equal(["Leather Boots"], vm.VisibleNodes.OfType<ItemNode>().Select(n => n.Name));
    }

    [Fact]
    public async Task SelectingACategoryRowNeverToggles()
    {
        // Toggling happens through the row's button only; list selection
        // landing on a header (keyboard navigation, or the ListBox re-asserting
        // a click) must not expand or collapse anything.
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        var gear = vm.VisibleNodes.OfType<CategoryNode>().First(c => c.Label == "Gear");
        vm.SelectedNode = gear;
        Assert.False(gear.IsExpanded);
        Assert.Equal(3, vm.VisibleNodes.Count);
        Assert.Null(vm.SelectedItem);

        // Selection re-asserted on the same header (what a ListBox does after
        // a click) still changes nothing.
        vm.SelectedNode = null;
        vm.SelectedNode = gear;
        Assert.False(gear.IsExpanded);
    }

    [Fact]
    public async Task TogglingWhileSearchingIsIgnored()
    {
        // During a search every header with matches is force-expanded; a
        // toggle would only flip hidden state that pops up after clearing.
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        vm.SearchText = "cot";
        var gear = vm.VisibleNodes.OfType<CategoryNode>().First(c => c.Label == "Gear");
        vm.ToggleCategoryCommand.Execute(gear);
        Assert.False(gear.IsExpanded);
        Assert.Contains(vm.VisibleNodes.OfType<ItemNode>(), n => n.Name == "Cotton Tabard");

        vm.SearchText = "";
        Assert.Equal(3, vm.VisibleNodes.Count); // still fully collapsed
    }

    [Fact]
    public async Task SearchShowsMatchesUnderForcedOpenHeadersAndRestoresCollapseOnClear()
    {
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        vm.SearchText = "cot";
        Assert.Equal(["Cotton Tabard"], vm.VisibleNodes.OfType<ItemNode>().Select(n => n.Name));
        // Headers of empty categories are hidden entirely.
        Assert.Equal(["Gear", "Body"], vm.VisibleNodes.OfType<CategoryNode>().Select(c => c.Label));

        vm.SearchText = "face";
        Assert.Equal(["Midlander ♂ Face 1"], vm.VisibleNodes.OfType<ItemNode>().Select(n => n.Name));
        Assert.Equal(["Body", "Faces"], vm.VisibleNodes.OfType<CategoryNode>().Select(c => c.Label));

        // Clearing the search returns to the collapsed default.
        vm.SearchText = "  ";
        Assert.Equal(3, vm.VisibleNodes.Count);
        Assert.All(vm.VisibleNodes.OfType<CategoryNode>(), c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public async Task SelectingItemPublishesItsModel()
    {
        var loader = new FakeLoader();
        var vm = Create(MixedItems(), loader);
        await vm.LoadItemsAsync();

        RenderModel? published = null;
        vm.ModelLoaded += m => published = m;

        vm.SearchText = "Hempen";
        vm.SelectedNode = NodeFor(vm, "Hempen Camise");
        await WaitUntil(() => published is not null);

        Assert.NotNull(published);
        Assert.Equal([1u], loader.Loaded);
        Assert.Contains("Hempen", vm.StatusText);

        // Hiding the selected row (collapse via cleared search) keeps the selection.
        vm.SearchText = "";
        Assert.Equal("Hempen Camise", vm.SelectedItem?.Name);
    }

    [Fact]
    public async Task RevealAndSelectExpandsTheItemsCategories()
    {
        var vm = Create(MixedItems());
        await vm.LoadItemsAsync();

        var ring = MixedItems().First(i => i.Slot == EquipSlot.RightRing);
        vm.RevealAndSelect(ring);

        Assert.Equal("Bronze Ring", vm.SelectedItem?.Name);
        Assert.Equal("Bronze Ring", (vm.SelectedNode as ItemNode)?.Name);
        Assert.Contains(vm.VisibleNodes.OfType<CategoryNode>(), c => c.Label == "Rings");
    }

    [Fact]
    public async Task RapidReselectionOnlyPublishesTheNewestModel()
    {
        var loader = new FakeLoader { Delay = TimeSpan.FromMilliseconds(150) };
        var vm = Create(MixedItems(), loader);
        await vm.LoadItemsAsync();

        var published = new List<RenderModel?>();
        vm.ModelLoaded += published.Add;

        vm.SearchText = "e";
        vm.SelectedNode = NodeFor(vm, "Hempen Camise");
        await Task.Delay(20);
        vm.SelectedNode = NodeFor(vm, "Leather Boots"); // cancels the first load

        await WaitUntil(() => published.Count > 0);
        await Task.Delay(300); // give a stale first load every chance to sneak in

        Assert.Single(published);
        Assert.Contains("Leather Boots", vm.StatusText);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not reached in time");
    }
}
