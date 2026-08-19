using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;
using Moonlace.GameData.ModTools;
using Moonlace.GameData.Resolution;
using Moonlace.GameData.Upgrade;

namespace Moonlace.App.ViewModels;

/// <summary>One pickable retarget destination: an item plus a disambiguating label.</summary>
public sealed class DestinationChoice
{
    public required EquipmentItem Item { get; init; }

    public string Label => $"{Item.Name} · {(Item.IsAccessory ? 'a' : 'e')}{Item.ModelId:D4} v{Item.Variant}";
}

/// <summary>
/// The Mod tools menu in the top bar. "Retarget mod…" analyzes what gear a
/// modpack binds to and rewires a chosen model onto a different item and/or
/// race/gender, saved as a new standalone .pmp — the input modpack is never
/// modified.
/// </summary>
public partial class ModToolsViewModel : ViewModelBase
{
    private const int MaxDestinationChoices = 60;

    private readonly ModRetargeter _retargeter;
    private readonly IItemRepository _items;
    private readonly IFilePickerService _files;
    private readonly ILogger<ModToolsViewModel> _logger;

    private string? _modpackPath;
    private IReadOnlyList<EquipmentItem>? _allItems;

    [ObservableProperty]
    private bool _isRetargetPanelOpen;

    [ObservableProperty]
    private string _modName = "";

    public ObservableCollection<ModBinding> Bindings { get; } = [];

    [ObservableProperty]
    private bool _hasBindings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRetargetedCommand))]
    private ModBinding? _selectedBinding;

    [ObservableProperty]
    private string _destinationSearch = "";

    public ObservableCollection<DestinationChoice> DestinationChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRetargetedCommand))]
    private DestinationChoice? _selectedDestination;

    public IReadOnlyList<RaceVariant> DestinationRaces { get; } = AssetPathResolver.KnownRaces;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRetargetedCommand))]
    private RaceVariant? _selectedDestinationRace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRetargetedCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _warningsText;

    public ModToolsViewModel(
        ModRetargeter retargeter,
        IItemRepository items,
        IFilePickerService files,
        ILogger<ModToolsViewModel> logger)
    {
        _retargeter = retargeter;
        _items = items;
        _files = files;
        _logger = logger;
    }

    partial void OnSelectedBindingChanged(ModBinding? value) => _ = RefreshDestinationsAsync();

    partial void OnDestinationSearchChanged(string value) => _ = RefreshDestinationsAsync();

    [RelayCommand]
    private async Task OpenRetargetAsync()
    {
        ErrorText = null;
        var modpack = await _files.OpenFileAsync(
            "Select a modpack to retarget", "Modpacks", ModpackFile.PickerPatterns);
        if (modpack is null)
            return;

        await AnalyzeAsync(modpack);
    }

    private async Task AnalyzeAsync(string modpack)
    {
        IsBusy = true;
        BusyText = "Analyzing mod…";
        try
        {
            var analysis = await _retargeter.AnalyzeAsync(modpack);
            _modpackPath = modpack;
            ModName = analysis.ModName;
            ResultText = null;
            WarningsText = analysis.Notes.Count == 0 ? null : string.Join("\n", analysis.Notes);

            Bindings.Clear();
            foreach (var binding in analysis.Bindings)
                Bindings.Add(binding);
            HasBindings = Bindings.Count > 0;
            SelectedBinding = Bindings.FirstOrDefault();
            SelectedDestinationRace = null;
            DestinationSearch = "";
            IsRetargetPanelOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modpack analysis failed for {Modpack}", modpack);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Items that can replace the selected binding: same equip slot, filtered by the search text.</summary>
    private async Task RefreshDestinationsAsync()
    {
        var binding = SelectedBinding;
        if (binding is null)
        {
            DestinationChoices.Clear();
            SelectedDestination = null;
            return;
        }

        _allItems ??= await _items.GetEquipmentItemsAsync();
        var search = DestinationSearch.Trim();
        var matches = _allItems
            .Where(i => !i.IsWeapon && !i.IsBodyPart && i.Slot == binding.Slot)
            .Where(i => search.Length == 0 || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(MaxDestinationChoices)
            .ToArray();

        // The list may have been retriggered while items loaded; latest state wins.
        if (!ReferenceEquals(binding, SelectedBinding))
            return;

        var kept = SelectedDestination?.Item;
        DestinationChoices.Clear();
        foreach (var item in matches)
            DestinationChoices.Add(new DestinationChoice { Item = item });
        SelectedDestination = kept is null ? null : DestinationChoices.FirstOrDefault(c => c.Item.RowId == kept.RowId);
    }

    private bool CanSaveRetargeted =>
        !IsBusy && SelectedBinding is not null && SelectedDestination is not null && SelectedDestinationRace is not null;

    [RelayCommand(CanExecute = nameof(CanSaveRetargeted))]
    private async Task SaveRetargetedAsync()
    {
        if (_modpackPath is null || SelectedBinding is not { } binding
            || SelectedDestination is not { } destination || SelectedDestinationRace is not { } race)
            return;

        ErrorText = null;
        var output = await _files.SaveFileAsync(
            "Save retargeted modpack",
            $"{ModName} ({destination.Item.Name}).pmp",
            "Penumbra Mod Package", ["*.pmp"]);
        if (output is null)
            return;

        IsBusy = true;
        BusyText = "Retargeting mod…";
        try
        {
            var report = await _retargeter.RetargetAsync(_modpackPath, binding, destination.Item, race.Code, output);
            ResultText = report.Summary() + $"\nSaved to {output}";
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retarget failed for {Modpack}", _modpackPath);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseRetargetPanel() => IsRetargetPanelOpen = false;

    /// <summary>
    /// Dev/testing hook: runs the whole retarget flow headlessly from a spec
    /// "modpack|destination item name|race code|output path", using the
    /// modpack's first binding.
    /// </summary>
    public async Task RetargetHeadlessAsync(string spec)
    {
        var parts = spec.Split('|');
        if (parts.Length != 4)
        {
            ErrorText = "MOONLACE_AUTORETARGET needs \"modpack|item name|race code|output path\".";
            return;
        }

        await AnalyzeAsync(parts[0]);
        if (SelectedBinding is not { } binding)
        {
            ErrorText ??= "The modpack has no retargetable bindings.";
            return;
        }

        try
        {
            _allItems ??= await _items.GetEquipmentItemsAsync();
            var destination = _allItems.First(i => i.Name == parts[1] && i.Slot == binding.Slot);
            var report = await _retargeter.RetargetAsync(parts[0], binding, destination, parts[2], parts[3]);
            ResultText = report.Summary() + $"\nSaved to {parts[3]}";
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
            _logger.LogInformation("Headless retarget: {Summary}", report.Summary());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless retarget failed");
            ErrorText = ex.Message;
        }
    }
}
