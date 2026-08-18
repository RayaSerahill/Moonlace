using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.App.Services;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Services;

namespace Moonlace.App.ViewModels;

/// <summary>
/// First-launch (or invalid-path) view: pick and validate the FFXIV installation.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IFolderPickerService _folderPicker;
    private readonly ILogger<SetupViewModel> _logger;

    [ObservableProperty]
    private string _gamePath = "";

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised with the resolved game directory once validation succeeds.</summary>
    public event Action<string>? InstallationConfirmed;

    public SetupViewModel(ISettingsService settings, IFolderPickerService folderPicker, ILogger<SetupViewModel> logger)
    {
        _settings = settings;
        _folderPicker = folderPicker;
        _logger = logger;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = await _folderPicker.PickFolderAsync("Locate your FINAL FANTASY XIV installation");
        if (folder is not null)
        {
            GamePath = folder;
            ErrorMessage = null;
        }
    }

    [RelayCommand]
    private void Continue()
    {
        var result = InstallationValidator.Validate(GamePath);
        if (!result.IsValid)
        {
            _logger.LogWarning("Rejected game path {Path}: {Error}", GamePath, result.Error);
            ErrorMessage = result.Error;
            return;
        }

        _logger.LogInformation("Game path validated: {Path}", result.GameDirectory);
        var settings = _settings.Load();
        settings.GamePath = result.GameDirectory;
        _settings.Save(settings);

        ErrorMessage = null;
        InstallationConfirmed?.Invoke(result.GameDirectory!);
    }
}
