using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.GameData.Upgrade;

namespace Moonlace.App.ViewModels;

/// <summary>
/// The Files menu in the top bar. "Upgrade to DT" takes a distributable
/// modpack — a Penumbra .pmp or a .ttmp/.ttmp2 — converts its legacy
/// (Endwalker) gear assets to Dawntrail formats, and writes a new upgraded
/// .pmp. The input file is never modified.
/// </summary>
public partial class FilesViewModel : ViewModelBase
{
    private readonly DawntrailModUpgrader _upgrader;
    private readonly IFilePickerService _files;
    private readonly ILogger<FilesViewModel> _logger;

    private string? _pendingModpack;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmModName = "";

    [ObservableProperty]
    private bool _isBusy;

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
        IFilePickerService files,
        ILogger<FilesViewModel> logger)
    {
        _upgrader = upgrader;
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
        ConfirmModName = await Task.Run(() => ModpackFile.PeekName(modpack));
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelUpgrade()
    {
        IsConfirmOpen = false;
        _pendingModpack = null;
    }

    [RelayCommand]
    private async Task ConfirmUpgradeAsync()
    {
        if (_pendingModpack is not { } modpack)
            return;

        IsConfirmOpen = false;
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

    [RelayCommand]
    private void CloseSummary() => IsSummaryOpen = false;
}
