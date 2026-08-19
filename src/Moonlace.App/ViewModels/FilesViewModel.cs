using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.GameData.Import;
using Moonlace.GameData.Upgrade;

namespace Moonlace.App.ViewModels;

/// <summary>
/// The Files menu in the top bar. "Upgrade to DT" takes a distributable
/// modpack a Penumbra .pmp or a .ttmp/.ttmp2 converts its legacy
/// (Endwalker) gear assets to Dawntrail formats, and writes a new upgraded
/// .pmp. "Import mod" takes the same modpack formats and applies their files
/// as edits: into the active item's session, or into the linked Penumbra mod
/// while live editing. The input file is never modified either way.
/// </summary>
public partial class FilesViewModel : ViewModelBase
{
    private enum PendingAction
    {
        UpgradeToDt,
        ImportMod,
    }

    private readonly DawntrailModUpgrader _upgrader;
    private readonly ModpackImporter _importer;
    private readonly IFilePickerService _files;
    private readonly ILogger<FilesViewModel> _logger;

    private string? _pendingModpack;
    private PendingAction _pendingAction;

    /// <summary>Raised after an import changed effective assets, so the viewport and tabs reload.</summary>
    public event Action? AssetsChanged;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmTitle = "";

    [ObservableProperty]
    private string _confirmQuestion = "";

    [ObservableProperty]
    private string _confirmBody = "";

    [ObservableProperty]
    private string _confirmButtonText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private bool _isSummaryOpen;

    [ObservableProperty]
    private string _summaryTitle = "";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string? _warningsText;

    [ObservableProperty]
    private string? _errorText;

    public FilesViewModel(
        DawntrailModUpgrader upgrader,
        ModpackImporter importer,
        IFilePickerService files,
        ILogger<FilesViewModel> logger)
    {
        _upgrader = upgrader;
        _importer = importer;
        _files = files;
        _logger = logger;
    }

    [RelayCommand]
    private async Task UpgradeToDtAsync()
    {
        ErrorText = null;
        var modpack = await _files.OpenFileAsync(
            "Select a modpack to upgrade to Dawntrail", "Modpacks", ModpackFile.PickerPatterns);
        if (modpack is null)
            return;

        _pendingModpack = modpack;
        _pendingAction = PendingAction.UpgradeToDt;
        var name = await Task.Run(() => ModpackFile.PeekName(modpack));
        ConfirmTitle = "Upgrade to Dawntrail";
        ConfirmQuestion = $"Upgrade “{name}”?";
        ConfirmBody =
            "A new upgraded .pmp is written the selected modpack itself is not touched. " +
            "Legacy (Endwalker) gear materials are converted to Dawntrail formats: materials become " +
            "characterlegacy with 32-row color tables, index textures are generated from the old normal " +
            "maps, and mask/normal channels are moved. Skin, hair and already-current materials are left " +
            "alone; .meta/.rgsp metadata entries are not carried over.";
        ConfirmButtonText = "Choose output…";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ImportModAsync()
    {
        ErrorText = null;
        if (_importer.DescribeDestination() is null)
        {
            ErrorText = "Select an item first without a Penumbra link, imported files become that item's session edits.";
            return;
        }

        var modpack = await _files.OpenFileAsync(
            "Select a modpack to import as edits", "Modpacks", ModpackFile.PickerPatterns);
        if (modpack is null)
            return;

        // The destination can have changed while the picker was open.
        if (_importer.DescribeDestination() is not { } destination)
        {
            ErrorText = "Select an item first without a Penumbra link, imported files become that item's session edits.";
            return;
        }

        _pendingModpack = modpack;
        _pendingAction = PendingAction.ImportMod;
        var name = await Task.Run(() => ModpackFile.PeekName(modpack));
        ConfirmTitle = "Import mod";
        ConfirmQuestion = $"Import “{name}” into {destination}?";
        ConfirmBody =
            "The modpack's files (with its default option selection) become edits, exactly as if you had " +
            "made them here the modpack file itself is not touched. Session edits stay in Moonlace's " +
            "workspace until you export them; edits to a linked mod are live in the mod folder and " +
            "revertible from the Penumbra menu. Metadata manipulations (IMC/EQP/EST…) are not imported.";
        ConfirmButtonText = "Import";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _pendingModpack = null;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_pendingModpack is not { } modpack)
            return;

        IsConfirmOpen = false;
        if (_pendingAction == PendingAction.ImportMod)
        {
            await RunImportAsync(modpack);
            return;
        }

        var output = await _files.SaveFileAsync(
            "Save upgraded modpack",
            Path.GetFileNameWithoutExtension(modpack) + " (DT).pmp",
            "Penumbra Mod Package", ["*.pmp"]);
        if (output is null)
        {
            _pendingModpack = null;
            return;
        }

        IsBusy = true;
        BusyText = "Upgrading mod…";
        try
        {
            var report = await _upgrader.UpgradeModpackAsync(modpack, output);
            SummaryTitle = report.AnyChanges
                ? $"Upgraded “{report.ModName}” to Dawntrail"
                : $"“{report.ModName}” repackaged";
            SummaryText = report.Summary() + $"\n\nSaved to {output}";
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
            IsSummaryOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dawntrail upgrade failed for {Modpack}", modpack);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _pendingModpack = null;
        }
    }

    /// <summary>Dev/testing hook: true when an import destination (item session or Penumbra link) exists.</summary>
    public bool CanImportNow => _importer.DescribeDestination() is not null;

    /// <summary>Dev/testing hook: runs an import headlessly, without the picker and confirm dialogs.</summary>
    public Task ImportModpackAsync(string modpackPath) => RunImportAsync(modpackPath);

    private async Task RunImportAsync(string modpack)
    {
        IsBusy = true;
        BusyText = "Importing mod…";
        try
        {
            var report = await _importer.ImportAsync(modpack);
            SummaryTitle = $"Imported “{report.ModName}”";
            SummaryText = report.Summary();
            WarningsText = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
            IsSummaryOpen = true;
            if (report.FilesImported > 0)
                AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modpack import failed for {Modpack}", modpack);
            ErrorText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _pendingModpack = null;
        }
    }

    [RelayCommand]
    private void CloseSummary() => IsSummaryOpen = false;
}
