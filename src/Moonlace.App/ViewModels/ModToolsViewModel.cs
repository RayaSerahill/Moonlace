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

    public string Label => $"{Item.Name} · {Item.Slot} · {(Item.IsAccessory ? 'a' : 'e')}{Item.ModelId:D4} v{Item.Variant}";
}

/// <summary>
/// One row of the retarget panel's left column: a modded model and the new
/// target (item + race) the user has picked for it, if any.
/// </summary>
public partial class RetargetAssignmentViewModel : ObservableObject
{
    public RetargetAssignmentViewModel(ModBinding binding)
    {
        Binding = binding;
    }

    public ModBinding Binding { get; }

    public string BindingLabel => Binding.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(IsAssigned))]
    private DestinationChoice? _destination;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private RaceVariant? _race;

    public bool IsAssigned => Destination is not null;

    public string TargetLabel => Destination is null
        ? "→ unchanged"
        : $"→ {Destination.Item.Name} · {Race?.Label ?? Binding.RaceLabel}";
}

/// <summary>
/// The Mod tools menu in the top bar. "Retarget mod…" analyzes what gear a
/// modpack binds to and lets the user rewire each modded model onto its own
/// new item and/or race/gender, saved together as a new standalone .pmp.
/// The input modpack is never modified.
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
    private bool _syncingSelection;

    [ObservableProperty]
    private bool _isRetargetPanelOpen;

    [ObservableProperty]
    private string _modName = "";

    /// <summary>Left column: every modded model with its (possibly empty) new target.</summary>
    public ObservableCollection<RetargetAssignmentViewModel> Assignments { get; } = [];

    [ObservableProperty]
    private bool _hasBindings;

    [ObservableProperty]
    private RetargetAssignmentViewModel? _selectedAssignment;

    [ObservableProperty]
    private string _destinationSearch = "";

    public ObservableCollection<DestinationChoice> DestinationChoices { get; } = [];

    [ObservableProperty]
    private DestinationChoice? _selectedDestination;

    public IReadOnlyList<RaceVariant> DestinationRaces { get; } = AssetPathResolver.KnownRaces;

    [ObservableProperty]
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

            Assignments.Clear();
            foreach (var binding in analysis.Bindings)
                Assignments.Add(new RetargetAssignmentViewModel(binding));
            HasBindings = Assignments.Count > 0;
            SelectedAssignment = Assignments.FirstOrDefault();
            IsRetargetPanelOpen = true;
            SaveRetargetedCommand.NotifyCanExecuteChanged();
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

    /// <summary>Selecting a row on the left loads its stored target into the pickers on the right.</summary>
    partial void OnSelectedAssignmentChanged(RetargetAssignmentViewModel? value)
    {
        _syncingSelection = true;
        DestinationSearch = "";
        SelectedDestinationRace = value?.Race;
        _syncingSelection = false;
        _ = RefreshDestinationsAsync();
    }

    partial void OnDestinationSearchChanged(string value)
    {
        if (!_syncingSelection)
            _ = RefreshDestinationsAsync();
    }

    /// <summary>Picking an item on the right stores it on the selected row.</summary>
    partial void OnSelectedDestinationChanged(DestinationChoice? value)
    {
        if (_syncingSelection || SelectedAssignment is not { } assignment)
            return;

        assignment.Destination = value;
        // Picking an item without a race keeps the model's own race.
        if (value is not null && assignment.Race is null)
        {
            var sourceRace = DestinationRaces.FirstOrDefault(r => r.Code == assignment.Binding.RaceCode);
            _syncingSelection = true;
            SelectedDestinationRace = sourceRace;
            _syncingSelection = false;
            assignment.Race = sourceRace;
        }

        SaveRetargetedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDestinationRaceChanged(RaceVariant? value)
    {
        if (_syncingSelection || SelectedAssignment is not { } assignment)
            return;
        assignment.Race = value;
    }

    /// <summary>
    /// Items that can replace the selected model: any gear or accessory slot
    /// (same-slot matches listed first), filtered by the search text.
    /// </summary>
    private async Task RefreshDestinationsAsync()
    {
        var assignment = SelectedAssignment;
        if (assignment is null)
        {
            DestinationChoices.Clear();
            SelectedDestination = null;
            return;
        }

        _allItems ??= await _items.GetEquipmentItemsAsync();
        var search = DestinationSearch.Trim();
        var matches = _allItems
            .Where(i => !i.IsWeapon && !i.IsBodyPart)
            .Where(i => search.Length == 0 || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Slot == assignment.Binding.Slot)
            .Take(MaxDestinationChoices)
            .ToArray();

        // The list may have been retriggered while items loaded; latest state wins.
        if (!ReferenceEquals(assignment, SelectedAssignment))
            return;

        _syncingSelection = true;
        DestinationChoices.Clear();
        foreach (var item in matches)
            DestinationChoices.Add(new DestinationChoice { Item = item });

        // Keep the row's stored target selected, even when the filter hides it.
        var stored = assignment.Destination;
        if (stored is not null)
        {
            var match = DestinationChoices.FirstOrDefault(c => c.Item.RowId == stored.Item.RowId);
            if (match is null)
                DestinationChoices.Insert(0, stored);
            SelectedDestination = match ?? stored;
        }
        else
        {
            SelectedDestination = null;
        }

        _syncingSelection = false;
    }

    /// <summary>Clears the selected row's target so its files are carried unchanged.</summary>
    [RelayCommand]
    private void ClearAssignment()
    {
        if (SelectedAssignment is not { } assignment)
            return;

        assignment.Destination = null;
        assignment.Race = null;
        _syncingSelection = true;
        SelectedDestination = null;
        SelectedDestinationRace = null;
        _syncingSelection = false;
        SaveRetargetedCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveRetargeted => !IsBusy && Assignments.Any(a => a.IsAssigned);

    [RelayCommand(CanExecute = nameof(CanSaveRetargeted))]
    private async Task SaveRetargetedAsync()
    {
        if (_modpackPath is null)
            return;

        var assignments = Assignments
            .Where(a => a.Destination is not null)
            .Select(a => new RetargetAssignment(
                a.Binding,
                a.Destination!.Item,
                (a.Race ?? DestinationRaces.First(r => r.Code == a.Binding.RaceCode)).Code))
            .ToArray();
        if (assignments.Length == 0)
            return;

        ErrorText = null;
        var suggestedName = assignments.Length == 1
            ? $"{ModName} ({assignments[0].Destination.Name}).pmp"
            : $"{ModName} (retargeted).pmp";
        var output = await _files.SaveFileAsync(
            "Save retargeted modpack", suggestedName, "Penumbra Mod Package", ["*.pmp"]);
        if (output is null)
            return;

        IsBusy = true;
        BusyText = "Retargeting mod…";
        try
        {
            var report = await _retargeter.RetargetAsync(_modpackPath, assignments, output);
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
    /// "modpack|destination item name|race code|output path", assigning the
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
        if (SelectedAssignment is not { } assignment)
        {
            ErrorText ??= "The modpack has no retargetable bindings.";
            return;
        }

        try
        {
            _allItems ??= await _items.GetEquipmentItemsAsync();
            var destination = _allItems
                .Where(i => !i.IsWeapon && !i.IsBodyPart && i.Name == parts[1])
                .OrderByDescending(i => i.Slot == assignment.Binding.Slot)
                .First();
            assignment.Destination = new DestinationChoice { Item = destination };
            assignment.Race = DestinationRaces.FirstOrDefault(r => r.Code == parts[2]);
            SaveRetargetedCommand.NotifyCanExecuteChanged();
            var report = await _retargeter.RetargetAsync(
                parts[0], [new RetargetAssignment(assignment.Binding, destination, parts[2])], parts[3]);
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
