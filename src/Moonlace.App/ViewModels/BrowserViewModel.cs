using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;

namespace Moonlace.App.ViewModels;

/// <summary>
/// The item browser: a categorized, collapsible tree (Gear / Accessories /
/// Body, each with slot subcategories) flattened into one virtualized list,
/// plus search and selection. Selecting an item loads its render model
/// asynchronously; rapid re-selection cancels the previous load so a slow
/// older load can never overwrite a newer selection.
/// </summary>
public partial class BrowserViewModel : ViewModelBase
{
    private sealed record SubcategorySpec(string Label, EquipSlot[] Slots);

    private sealed record CategorySpec(string Label, SubcategorySpec[] Subcategories);

    private static readonly CategorySpec[] CategoryLayout =
    [
        new("Gear",
        [
            new("Weapons", [EquipSlot.MainHand]),
            new("Off-hands", [EquipSlot.OffHand]),
            new("Head", [EquipSlot.Head]),
            new("Body", [EquipSlot.Body]),
            new("Hands", [EquipSlot.Hands]),
            new("Legs", [EquipSlot.Legs]),
            new("Feet", [EquipSlot.Feet]),
        ]),
        new("Accessories",
        [
            new("Earrings", [EquipSlot.Ears]),
            new("Necklaces", [EquipSlot.Neck]),
            new("Bracelets", [EquipSlot.Wrists]),
            new("Rings", [EquipSlot.RightRing, EquipSlot.LeftRing]),
        ]),
        new("Body",
        [
            new("Faces", [EquipSlot.Face]),
            new("Hair", [EquipSlot.Hair]),
            new("Tails", [EquipSlot.Tail]),
            new("Bodies", [EquipSlot.HumanBody]),
        ]),
    ];

    private readonly IItemRepository _items;
    private readonly IRenderModelLoader _modelLoader;
    private readonly ILogger<BrowserViewModel> _logger;

    private IReadOnlyList<EquipmentItem> _allItems = [];
    private List<CategoryNode> _categories = [];
    private Dictionary<uint, (ItemNode Node, CategoryNode Main, CategoryNode Sub)> _nodesByRowId = [];
    private ItemNode? _selectedItemNode;
    private bool _syncingSelection;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>The flattened, filtered tree the list shows: CategoryNode and ItemNode rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<object> _visibleNodes = [];

    /// <summary>The list's selected row. Category rows toggle their expansion instead of selecting.</summary>
    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private EquipmentItem? _selectedItem;

    [ObservableProperty]
    private bool _isLoadingItems;

    [ObservableProperty]
    private bool _isLoadingModel;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _viewportMessage;

    /// <summary>The currently displayable model; null clears the viewport.</summary>
    public event Action<RenderModel?>? ModelLoaded;

    public EditorViewModel Editor { get; }

    public BrowserViewModel(
        IItemRepository items,
        IRenderModelLoader modelLoader,
        EditorViewModel editor,
        ILogger<BrowserViewModel> logger)
    {
        _items = items;
        _modelLoader = modelLoader;
        Editor = editor;
        _logger = logger;

        // Session edits (imported model, changed material/texture, discard)
        // re-run the item pipeline so the viewport shows the effective assets.
        Editor.SessionAssetsChanged += () => _ = LoadSelectedModelAsync(SelectedItem);
    }

    public async Task LoadItemsAsync()
    {
        IsLoadingItems = true;
        StatusText = "Loading equipment…";
        try
        {
            _allItems = await _items.GetEquipmentItemsAsync();
            BuildCategories();
            RebuildVisibleNodes();

            var gear = _allItems.Count(i => !i.IsAccessory && !i.IsBodyPart);
            var accessories = _allItems.Count(i => i.IsAccessory);
            var bodyParts = _allItems.Count(i => i.IsBodyPart);
            StatusText = $"{gear:N0} gear · {accessories:N0} accessories · {bodyParts:N0} body models";

            // Dev/testing hook: auto-select an item by name so the full
            // pipeline can be exercised without UI automation.
            var autoSelect = Environment.GetEnvironmentVariable("MOONLACE_AUTOSELECT");
            if (!string.IsNullOrEmpty(autoSelect))
            {
                var match = _allItems.FirstOrDefault(i =>
                    i.Name.Contains(autoSelect, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    RevealAndSelect(match);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load item list");
            StatusText = "Failed to load the item list. See the log for details.";
        }
        finally
        {
            IsLoadingItems = false;
        }
    }

    // --- Category tree ---

    private void BuildCategories()
    {
        _categories = [];
        _nodesByRowId = [];

        var bySlot = _allItems.ToLookup(i => i.Slot);
        foreach (var categorySpec in CategoryLayout)
        {
            var main = new CategoryNode { Label = categorySpec.Label, Level = 0 };
            foreach (var subSpec in categorySpec.Subcategories)
            {
                var sub = new CategoryNode { Label = subSpec.Label, Level = 1 };
                foreach (var item in subSpec.Slots.SelectMany(slot => bySlot[slot]))
                {
                    var node = new ItemNode(item);
                    sub.Items.Add(node);
                    _nodesByRowId[item.RowId] = (node, main, sub);
                }

                if (sub.Items.Count > 0)
                    main.Children.Add(sub);
            }

            if (main.Children.Count > 0)
                _categories.Add(main);
        }
    }

    /// <summary>
    /// Flattens the tree into the visible rows. A non-empty search shows all
    /// matches with their headers force-expanded (manual collapse state is
    /// kept untouched for when the search clears); otherwise expansion state
    /// decides, with everything collapsed by default.
    /// </summary>
    private void RebuildVisibleNodes()
    {
        var query = SearchText.Trim();
        var searching = query.Length > 0;
        var visible = new List<object>();

        foreach (var main in _categories)
        {
            var subs = new List<(CategoryNode Sub, IReadOnlyList<ItemNode> Items)>();
            foreach (var sub in main.Children)
            {
                IReadOnlyList<ItemNode> items = searching
                    ? sub.Items.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray()
                    : sub.Items;
                if (searching && items.Count == 0)
                    continue;
                subs.Add((sub, items));
            }

            if (searching && subs.Count == 0)
                continue;

            visible.Add(main);
            if (!searching && !main.IsExpanded)
                continue;

            foreach (var (sub, items) in subs)
            {
                visible.Add(sub);
                if (searching || sub.IsExpanded)
                    visible.AddRange(items);
            }
        }

        VisibleNodes = visible;
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleNodes();
        RestoreSelectedNode();
    }

    /// <summary>
    /// Expands/collapses a category row. This runs from the row's own button
    /// (which swallows the click), never from list selection toggling
    /// inside a selection-changed handler fights the ListBox's own click
    /// processing and double-toggles.
    /// </summary>
    [RelayCommand]
    private void ToggleCategory(CategoryNode category)
    {
        // While searching, headers are informational: everything with matches
        // is force-expanded, and a toggle would only change hidden state the
        // user cannot see (surprising them once the search clears).
        if (SearchText.Trim().Length > 0)
            return;

        category.IsExpanded = !category.IsExpanded;
        RebuildVisibleNodes();
        RestoreSelectedNode();
    }

    partial void OnSelectedNodeChanged(object? value)
    {
        if (_syncingSelection)
            return;

        // Only item rows carry a selection. Category rows toggle via their
        // button and are not click-selectable; keyboard navigation can still
        // land on one, which is a harmless highlight. A null (the selected
        // row was hidden by a collapse or search) keeps the item selection
        // and viewport intact.
        if (value is ItemNode node)
        {
            _selectedItemNode = node;
            SelectedItem = node.Item;
        }
    }

    /// <summary>Re-points the list selection at the selected item's row when it is visible, without side effects.</summary>
    private void RestoreSelectedNode()
    {
        _syncingSelection = true;
        try
        {
            SelectedNode = _selectedItemNode is not null && VisibleNodes.Contains(_selectedItemNode)
                ? _selectedItemNode
                : null;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>Expands the categories containing an item, selects it and scrolls it into the visible rows.</summary>
    public void RevealAndSelect(EquipmentItem item)
    {
        if (!_nodesByRowId.TryGetValue(item.RowId, out var entry))
        {
            SelectedItem = item;
            return;
        }

        entry.Main.IsExpanded = true;
        entry.Sub.IsExpanded = true;
        RebuildVisibleNodes();
        _selectedItemNode = entry.Node;
        RestoreSelectedNode();
        SelectedItem = item;
    }

    /// <summary>
    /// Reloads tabs and viewport after effective assets changed wholesale
    /// (Penumbra live-edit link/unlink, option change, revert).
    /// </summary>
    public async Task RefreshEffectiveAssetsAsync()
    {
        try
        {
            await Editor.RefreshTabsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh editor tabs after an effective-asset change");
        }

        await LoadSelectedModelAsync(SelectedItem);
    }

    partial void OnSelectedItemChanged(EquipmentItem? value)
    {
        _ = SelectItemAsync(value);
    }

    private async Task SelectItemAsync(EquipmentItem? item)
    {
        try
        {
            // Version selection must be resolved before the viewport model
            // loads, so the load uses the selected variant; the (slower) tab
            // refresh then runs alongside the model load.
            await Editor.PrepareItemAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare editor for item {Id}", item?.RowId);
        }

        var modelLoad = LoadSelectedModelAsync(item);
        try
        {
            await Editor.RefreshTabsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh editor tabs for item {Id}", item?.RowId);
        }

        await modelLoad;
    }

    private async Task LoadSelectedModelAsync(EquipmentItem? item)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        if (item is null)
        {
            ModelLoaded?.Invoke(null);
            ViewportMessage = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoadingModel = true;
        ViewportMessage = null;
        StatusText = $"Loading {item.Name}…";
        _logger.LogInformation("Selected item {Id}: {Name} ({Slot})", item.RowId, item.Name, item.Slot);

        try
        {
            var model = await _modelLoader.LoadAsync(item, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            ModelLoaded?.Invoke(model);
            var triangles = model.Meshes.Sum(m => m.Indices.Length) / 3;
            StatusText = $"{item.Name} {model.Meshes.Count} meshes, {triangles:N0} triangles";
        }
        catch (OperationCanceledException)
        {
            // A newer selection took over.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load model for item {Id} ({Name})", item.RowId, item.Name);
            if (!cts.IsCancellationRequested)
            {
                ModelLoaded?.Invoke(null);
                ViewportMessage = $"Unable to display this item.\n\n{ex.Message}";
                StatusText = item.Name;
            }
        }
        finally
        {
            if (_loadCts == cts)
                IsLoadingModel = false;
        }
    }
}
