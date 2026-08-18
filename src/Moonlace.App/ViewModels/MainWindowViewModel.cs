using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Services;

namespace Moonlace.App.ViewModels;

/// <summary>
/// Owns the top-level application state: setup vs. browser, and the game data
/// initialization in between.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IGameDataService _gameData;
    private readonly ILogger<MainWindowViewModel> _logger;

    public SetupViewModel Setup { get; }

    public BrowserViewModel Browser { get; }

    public PenumbraViewModel Penumbra { get; }

    public FilesViewModel Files { get; }

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private bool _isInitializing;

    public MainWindowViewModel(
        ISettingsService settings,
        IGameDataService gameData,
        SetupViewModel setup,
        BrowserViewModel browser,
        PenumbraViewModel penumbra,
        FilesViewModel files,
        ILogger<MainWindowViewModel> logger)
    {
        _settings = settings;
        _gameData = gameData;
        Setup = setup;
        Browser = browser;
        Penumbra = penumbra;
        Files = files;
        _logger = logger;

        _currentView = setup;
        Setup.InstallationConfirmed += gameDir => _ = InitializeGameDataAsync(gameDir);
        Penumbra.AssetsChanged += () => _ = Browser.RefreshEffectiveAssetsAsync();
        Files.AssetsChanged += () => _ = Browser.RefreshEffectiveAssetsAsync();
    }

    /// <summary>Called once at startup: revalidate the saved path and skip setup when it holds.</summary>
    public async Task StartAsync()
    {
        var saved = _settings.Load().GamePath;
        var result = InstallationValidator.Validate(saved);
        if (result.IsValid)
        {
            _logger.LogInformation("Saved game path is valid: {Path}", result.GameDirectory);
            await InitializeGameDataAsync(result.GameDirectory!);
        }
        else
        {
            _logger.LogInformation("No valid saved game path ({Reason}); showing setup", result.Error);
            Setup.GamePath = saved ?? "";
            CurrentView = Setup;
        }
    }

    private async Task InitializeGameDataAsync(string gameDirectory)
    {
        IsInitializing = true;
        try
        {
            await _gameData.InitializeAsync(gameDirectory);
            CurrentView = Browser;
            await Browser.LoadItemsAsync();

            // Dev/testing hook: link a Penumbra mod (default options) headlessly.
            var autoMod = Environment.GetEnvironmentVariable("MOONLACE_AUTOPENUMBRA");
            if (!string.IsNullOrEmpty(autoMod))
                await Penumbra.LinkWithDefaultsAsync(autoMod);

            // Dev/testing hook: import a modpack as edits once a destination
            // (item selection or Penumbra link) exists.
            var autoImport = Environment.GetEnvironmentVariable("MOONLACE_AUTOIMPORT");
            if (!string.IsNullOrEmpty(autoImport))
            {
                for (var i = 0; i < 100 && !Files.CanImportNow; i++)
                    await Task.Delay(100);
                await Files.ImportModpackAsync(autoImport);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game data initialization failed for {Path}", gameDirectory);
            Setup.ErrorMessage =
                "Could not read game data from this installation. " +
                "Make sure the directory is a complete FFXIV installation.";
            CurrentView = Setup;
        }
        finally
        {
            IsInitializing = false;
        }
    }
}
