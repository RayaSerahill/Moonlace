using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.GameData.ModTools;
using Moonlace.GameData.Upgrade;

namespace Moonlace.App.ViewModels;

/// <summary>
/// One row of the animation retarget panel's left column: a modded animation
/// and the emote/animation the user has picked for it, if any.
/// </summary>
public partial class AnimationAssignmentViewModel : ObservableObject
{
    public AnimationAssignmentViewModel(AnimationBinding binding)
    {
        Binding = binding;
    }

    public AnimationBinding Binding { get; }

    public string BindingLabel => Binding.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    [NotifyPropertyChangedFor(nameof(IsAssigned))]
    private AnimationTimeline? _destination;

    public bool IsAssigned => Destination is not null;

    public string TargetLabel => Destination is null
        ? "→ unchanged"
        : $"→ {Destination.DisplayName}";
}

/// <summary>
/// "Retarget animation…" in the Mod tools menu: analyzes which emotes and
/// animations a modpack replaces and lets the user rewire each one onto a
/// different emote/animation, saved together as a new standalone .pmp.
/// The input modpack is never modified.
/// </summary>
public partial class AnimationToolsViewModel : ViewModelBase
{
    private const int MaxDestinationChoices = 60;

    private readonly AnimationRetargeter _retargeter;
    private readonly AnimationTimelineCatalog _catalog;
    private readonly IFilePickerService _files;
    private readonly ILogger<AnimationToolsViewModel> _logger;

    private string? _modpackPath;
    private bool _syncingSelection;

    [ObservableProperty]
    private bool _isRetargetPanelOpen;

    [ObservableProperty]
    private string _modName = "";

    /// <summary>Left column: every modded animation with its (possibly empty) new target.</summary>
    public ObservableCollection<AnimationAssignmentViewModel> Assignments { get; } = [];

    [ObservableProperty]
    private bool _hasBindings;

    [ObservableProperty]
    private AnimationAssignmentViewModel? _selectedAssignment;

    [ObservableProperty]
    private string _destinationSearch = "";

    public ObservableCollection<AnimationTimeline> DestinationChoices { get; } = [];

    [ObservableProperty]
    private AnimationTimeline? _selectedDestination;

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

    public AnimationToolsViewModel(
        AnimationRetargeter retargeter,
        AnimationTimelineCatalog catalog,
        IFilePickerService files,
        ILogger<AnimationToolsViewModel> logger)
    {
        _retargeter = retargeter;
        _catalog = catalog;
        _files = files;
        _logger = logger;
    }

    [RelayCommand]
    private async Task OpenRetargetAsync()
    {
        ErrorText = null;
        var modpack = await _files.OpenFileAsync(
            "Select an animation mod to retarget", "Modpacks", ModpackFile.PickerPatterns);
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
                Assignments.Add(new AnimationAssignmentViewModel(binding));
            HasBindings = Assignments.Count > 0;
            SelectedAssignment = Assignments.FirstOrDefault();
            IsRetargetPanelOpen = true;
            SaveRetargetedCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Animation modpack analysis failed for {Modpack}", modpack);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Selecting a row on the left loads its stored target into the picker on the right.</summary>
    partial void OnSelectedAssignmentChanged(AnimationAssignmentViewModel? value)
    {
        _syncingSelection = true;
        DestinationSearch = "";
        _syncingSelection = false;
        RefreshDestinations();
    }

    partial void OnDestinationSearchChanged(string value)
    {
        if (!_syncingSelection)
            RefreshDestinations();
    }

    /// <summary>Picking a destination on the right stores it on the selected row.</summary>
    partial void OnSelectedDestinationChanged(AnimationTimeline? value)
    {
        if (_syncingSelection || SelectedAssignment is not { } assignment)
            return;

        assignment.Destination = value;
        SaveRetargetedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Emotes and animations the selected one can move onto, filtered by the
    /// search text. A search containing "/" also matches raw timeline keys
    /// (battle animations, facials, …) beyond the curated emote list.
    /// </summary>
    private void RefreshDestinations()
    {
        var assignment = SelectedAssignment;
        if (assignment is null)
        {
            DestinationChoices.Clear();
            SelectedDestination = null;
            return;
        }

        var search = DestinationSearch.Trim();
        var matches = _catalog.Destinations
            .Where(d => search.Length == 0
                || d.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || d.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(MaxDestinationChoices)
            .ToList();
        if (search.Contains('/') && matches.Count < MaxDestinationChoices)
        {
            var seen = matches.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
            matches.AddRange(_catalog.SearchRawKeys(search.ToLowerInvariant())
                .Where(k => !seen.Contains(k))
                .Take(MaxDestinationChoices - matches.Count)
                .Select(_catalog.Describe));
        }

        _syncingSelection = true;
        DestinationChoices.Clear();
        foreach (var choice in matches)
            DestinationChoices.Add(choice);

        // Keep the row's stored target selected, even when the filter hides it.
        var stored = assignment.Destination;
        if (stored is not null)
        {
            var match = DestinationChoices.FirstOrDefault(c => c.Key == stored.Key);
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
        _syncingSelection = true;
        SelectedDestination = null;
        _syncingSelection = false;
        SaveRetargetedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A destination's display name as a file name fragment: the slash
    /// command parenthetical goes ("Dance (/dance)" → "Dance"), and any
    /// slashes a raw timeline key carries become hyphens.
    /// </summary>
    internal static string FileNameSafeDestination(string displayName)
    {
        var parenthetical = displayName.IndexOf(" (/", StringComparison.Ordinal);
        if (parenthetical > 0 && displayName.EndsWith(')'))
            displayName = displayName[..parenthetical];
        return displayName.Replace('/', '-');
    }

    private bool CanSaveRetargeted => !IsBusy && Assignments.Any(a => a.IsAssigned);

    [RelayCommand(CanExecute = nameof(CanSaveRetargeted))]
    private async Task SaveRetargetedAsync()
    {
        if (_modpackPath is null)
            return;

        var assignments = Assignments
            .Where(a => a.Destination is not null)
            .Select(a => new AnimationRetargetAssignment(a.Binding, a.Destination!))
            .ToArray();
        if (assignments.Length == 0)
            return;

        ErrorText = null;
        var destinationNames = assignments
            .Select(a => FileNameSafeDestination(a.Destination.DisplayName))
            .Distinct()
            .ToArray();
        var suggestedName = $"{ModName} - {string.Join(" + ", destinationNames)}.pmp";
        var output = await _files.SaveFileAsync(
            "Save retargeted modpack", suggestedName, "Penumbra Mod Package", ["*.pmp"]);
        if (output is null)
            return;

        IsBusy = true;
        BusyText = "Retargeting animations…";
        try
        {
            var report = await _retargeter.RetargetAsync(_modpackPath, assignments, output);
            ResultText = report.Summary() + $"\nSaved to {output}";
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Animation retarget failed for {Modpack}", _modpackPath);
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
    /// Dev/testing hook: runs the whole flow headlessly from a spec
    /// "modpack|destination key or display name|output path", assigning the
    /// modpack's first binding.
    /// </summary>
    public async Task RetargetHeadlessAsync(string spec)
    {
        var parts = spec.Split('|');
        if (parts.Length != 3)
        {
            ErrorText = "MOONLACE_AUTORETARGETANIM needs \"modpack|destination key or name|output path\".";
            return;
        }

        await AnalyzeAsync(parts[0]);
        if (SelectedAssignment is not { } assignment)
        {
            ErrorText ??= "The modpack has no retargetable animations.";
            return;
        }

        try
        {
            var destination = _catalog.Destinations.FirstOrDefault(d => d.Key == parts[1])
                ?? _catalog.Destinations.First(d =>
                    d.DisplayName.Contains(parts[1], StringComparison.OrdinalIgnoreCase));
            assignment.Destination = destination;
            SaveRetargetedCommand.NotifyCanExecuteChanged();
            var report = await _retargeter.RetargetAsync(
                parts[0], [new AnimationRetargetAssignment(assignment.Binding, destination)], parts[2]);
            ResultText = report.Summary() + $"\nSaved to {parts[2]}";
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
            _logger.LogInformation("Headless animation retarget: {Summary}", report.Summary());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless animation retarget failed");
            ErrorText = ex.Message;
        }
    }
}
