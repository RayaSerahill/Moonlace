using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.Core.Penumbra;

namespace Moonlace.App.ViewModels;

/// <summary>
/// The Penumbra menu in the top bar: linking a mod folder for live editing,
/// choosing/changing which mod options the edits apply through, reverting the
/// run's edits from backups, and unlinking. The actual file work lives in
/// <see cref="IPenumbraLinkService"/>.
/// </summary>
public partial class PenumbraViewModel : ViewModelBase
{
    private readonly IPenumbraLinkService _link;
    private readonly IFolderPickerService _folders;
    private readonly ILogger<PenumbraViewModel> _logger;

    /// <summary>The mod folder waiting in the options panel before the first link; null when changing options of a linked mod.</summary>
    private string? _pendingDirectory;

    /// <summary>Raised when effective assets changed wholesale (link, options, revert, unlink) and the item view must reload.</summary>
    public event Action? AssetsChanged;

    [ObservableProperty]
    private bool _isLinked;

    [ObservableProperty]
    private bool _canRevert;

    [ObservableProperty]
    private string? _linkStatus;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isOptionsPanelOpen;

    [ObservableProperty]
    private string _optionsPanelTitle = "";

    [ObservableProperty]
    private bool _hasOptionGroups;

    [ObservableProperty]
    private bool _isConfirmingRevert;

    /// <summary>“Group · Option” label of the option currently capturing edits, or null for in-place editing.</summary>
    [ObservableProperty]
    private string? _editTargetLabel;

    public ObservableCollection<PenumbraGroupViewModel> OptionGroups { get; } = [];

    /// <summary>Where edits land, offered in the options panel: default files, or one of the mod's options.</summary>
    public ObservableCollection<EditTargetChoice> EditTargets { get; } = [];

    [ObservableProperty]
    private EditTargetChoice? _selectedEditTarget;

    // --- New option / group panel ---

    [ObservableProperty]
    private bool _isNewOptionPanelOpen;

    [ObservableProperty]
    private string _newOptionName = "";

    [ObservableProperty]
    private bool _createNewGroup;

    [ObservableProperty]
    private string _newGroupName = "";

    [ObservableProperty]
    private bool _newGroupIsMulti;

    [ObservableProperty]
    private bool _addDefaultFirstOption = true;

    [ObservableProperty]
    private bool _hasExistingGroups;

    [ObservableProperty]
    private string? _selectedExistingGroup;

    public ObservableCollection<string> ExistingGroups { get; } = [];

    public PenumbraViewModel(
        IPenumbraLinkService link,
        IFolderPickerService folders,
        ILogger<PenumbraViewModel> logger)
    {
        _link = link;
        _folders = folders;
        _logger = logger;

        _link.LinkChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateState);
        UpdateState();
    }

    private void UpdateState()
    {
        IsLinked = _link.IsLinked;
        CanRevert = _link.IsLinked && _link.ChangedFileCount > 0;
        if (!_link.IsLinked)
        {
            LinkStatus = null;
            EditTargetLabel = null;
            return;
        }

        var target = _link.EditTarget;
        EditTargetLabel = target is null ? null : $"{target.Group} · {target.Option}";

        var changed = _link.ChangedFileCount;
        var status = changed == 0
            ? $"Live editing “{_link.ModName}”"
            : $"Live editing “{_link.ModName}” - {changed} file{(changed == 1 ? "" : "s")} changed";
        if (target is not null)
            status += $" · edits → {target.Option}";
        LinkStatus = status;
    }

    // --- Live edit (link) ---

    [RelayCommand]
    private async Task StartLiveEditAsync()
    {
        ErrorText = null;
        var directory = await _folders.PickFolderAsync("Select an installed Penumbra mod folder");
        if (directory is null)
            return;

        await BeginLinkAsync(directory);
    }

    /// <summary>Inspects the mod; links immediately when it has no options, otherwise opens the option picker.</summary>
    public async Task BeginLinkAsync(string directory)
    {
        try
        {
            var info = await Task.Run(() => _link.Inspect(directory));
            if (info.Groups.Count == 0)
            {
                await Task.Run(() => _link.Link(directory, []));
                AssetsChanged?.Invoke();
                return;
            }

            _pendingDirectory = directory;
            OptionsPanelTitle = $"Link “{info.Name}”";
            PopulateGroups(info.Groups, info.Groups.Select(g => g.DefaultSelection()).ToArray());
            IsOptionsPanelOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link Penumbra mod at {Dir}", directory);
            ErrorText = ex.Message;
        }
    }

    /// <summary>Dev/testing hook: link a mod with its default options, no UI.</summary>
    public async Task LinkWithDefaultsAsync(string directory)
    {
        try
        {
            var info = await Task.Run(() => _link.Inspect(directory));
            await Task.Run(() => _link.Link(directory, info.Groups.Select(g => g.DefaultSelection()).ToArray()));
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-link failed for {Dir}", directory);
            ErrorText = ex.Message;
        }
    }

    // --- Options ---

    [RelayCommand]
    private void ChangeOptions()
    {
        if (!_link.IsLinked)
            return;

        ErrorText = null;
        _pendingDirectory = null;
        OptionsPanelTitle = "Change options";
        PopulateGroups(_link.Groups, _link.Selection);
        IsOptionsPanelOpen = true;
    }

    private void PopulateGroups(
        IReadOnlyList<PenumbraGroup> groups, IReadOnlyList<IReadOnlyList<int>> selection)
    {
        OptionGroups.Clear();
        for (var i = 0; i < groups.Count; i++)
            OptionGroups.Add(new PenumbraGroupViewModel(groups[i], i < selection.Count ? selection[i] : []));
        HasOptionGroups = OptionGroups.Count > 0;

        EditTargets.Clear();
        EditTargets.Add(new EditTargetChoice("Default files (edit in place)", null, null));
        foreach (var group in groups)
        {
            foreach (var option in group.Options)
                EditTargets.Add(new EditTargetChoice($"{group.Name} · {option.Name}", group.Name, option.Name));
        }

        var current = _link.EditTarget;
        SelectedEditTarget = current is null
            ? EditTargets[0]
            : EditTargets.FirstOrDefault(t => t.Group == current.Group && t.Option == current.Option) ?? EditTargets[0];
    }

    [RelayCommand]
    private async Task ApplyOptionsAsync()
    {
        var selection = OptionGroups.Select(g => g.BuildSelection()).ToArray();
        var editTarget = SelectedEditTarget;
        try
        {
            await Task.Run(() =>
            {
                if (_pendingDirectory is { } directory)
                    _link.Link(directory, selection);
                else
                    _link.SetSelection(selection);

                if (editTarget?.Group is not null)
                    _link.SetEditTarget(editTarget.Group, editTarget.Option!);
                else
                    _link.ClearEditTarget();
            });

            IsOptionsPanelOpen = false;
            _pendingDirectory = null;
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Penumbra options");
            ErrorText = ex.Message;
        }
    }

    // --- New option / group ---

    [RelayCommand]
    private void OpenNewOptionPanel()
    {
        if (!_link.IsLinked)
            return;

        ErrorText = null;
        NewOptionName = "";
        NewGroupName = "";
        NewGroupIsMulti = false;
        AddDefaultFirstOption = true;
        ExistingGroups.Clear();
        foreach (var group in _link.Groups)
            ExistingGroups.Add(group.Name);
        HasExistingGroups = ExistingGroups.Count > 0;
        SelectedExistingGroup = ExistingGroups.FirstOrDefault();
        CreateNewGroup = !HasExistingGroups;
        IsNewOptionPanelOpen = true;
    }

    [RelayCommand]
    private void CancelNewOption() => IsNewOptionPanelOpen = false;

    [RelayCommand]
    private async Task CreateOptionAsync()
    {
        var optionName = NewOptionName.Trim();
        var createGroup = CreateNewGroup;
        var groupName = createGroup ? NewGroupName.Trim() : SelectedExistingGroup;
        var isMulti = NewGroupIsMulti;
        var addDefault = AddDefaultFirstOption;
        try
        {
            if (string.IsNullOrEmpty(groupName))
                throw new PenumbraLinkException("Pick a group for the option (or create a new one).");

            await Task.Run(() =>
            {
                if (createGroup)
                {
                    _link.AddGroup(groupName, isMulti ? PenumbraGroupType.Multi : PenumbraGroupType.Single);
                    // Single groups always have one active option in Penumbra;
                    // an empty "Default" first option keeps the vanilla/default
                    // look selectable.
                    if (!isMulti && addDefault)
                        _link.AddOption(groupName, "Default");
                }

                _link.AddOption(groupName, optionName);
                // From here on, edits are captured as this option's own files;
                // everything unedited keeps coming from the always-active
                // default files.
                _link.SetEditTarget(groupName, optionName);
            });

            IsNewOptionPanelOpen = false;
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create option {Option} in group {Group}", optionName, groupName);
            ErrorText = ex.Message;
        }
    }

    /// <summary>Menu shortcut: stop capturing edits into an option, back to editing effective files in place.</summary>
    [RelayCommand]
    private void StopEditTarget()
    {
        _link.ClearEditTarget();
    }

    [RelayCommand]
    private void CancelOptions()
    {
        IsOptionsPanelOpen = false;
        _pendingDirectory = null;
    }

    // --- Revert ---

    [RelayCommand]
    private void RequestRevert() => IsConfirmingRevert = true;

    [RelayCommand]
    private void CancelRevert() => IsConfirmingRevert = false;

    [RelayCommand]
    private async Task ConfirmRevertAsync()
    {
        IsConfirmingRevert = false;
        try
        {
            await Task.Run(_link.RevertAll);
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revert failed");
            ErrorText = ex.Message;
        }
    }

    // --- Unlink ---

    [RelayCommand]
    private void Unlink()
    {
        if (!_link.IsLinked)
            return;

        _link.Unlink();
        AssetsChanged?.Invoke();
    }
}

/// <summary>A choice in the "capture edits in" picker: default files (null group) or a specific option.</summary>
public sealed record EditTargetChoice(string Label, string? Group, string? Option);

/// <summary>One option group in the picker: a dropdown for Single groups, checkboxes for Multi groups.</summary>
public partial class PenumbraGroupViewModel : ViewModelBase
{
    public string Name { get; }

    public bool IsSingle { get; }

    public bool IsMulti => !IsSingle;

    public ObservableCollection<PenumbraOptionChoiceViewModel> Options { get; } = [];

    [ObservableProperty]
    private PenumbraOptionChoiceViewModel? _selectedOption;

    public PenumbraGroupViewModel(PenumbraGroup group, IReadOnlyList<int> selected)
    {
        Name = group.Name;
        IsSingle = group.Type == PenumbraGroupType.Single;
        for (var i = 0; i < group.Options.Count; i++)
            Options.Add(new PenumbraOptionChoiceViewModel(i, group.Options[i].Name, selected.Contains(i)));

        if (IsSingle)
            SelectedOption = Options.FirstOrDefault(o => o.IsSelected) ?? Options.FirstOrDefault();
    }

    public int[] BuildSelection() => IsSingle
        ? SelectedOption is null ? [] : [SelectedOption.Index]
        : Options.Where(o => o.IsSelected).Select(o => o.Index).ToArray();
}

public partial class PenumbraOptionChoiceViewModel : ViewModelBase
{
    public int Index { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PenumbraOptionChoiceViewModel(int index, string name, bool isSelected)
    {
        Index = index;
        Name = name;
        _isSelected = isSelected;
    }
}
