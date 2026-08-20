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
/// Owns the top-level application state: setup vs. browser, and the game data
/// initialization in between.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IGameDataService _gameData;
    private readonly UpdateService _updates;
    private readonly ILogger<MainWindowViewModel> _logger;

    public SetupViewModel Setup { get; }

    public BrowserViewModel Browser { get; }

    public PenumbraViewModel Penumbra { get; }

    public FilesViewModel Files { get; }

    public ModToolsViewModel ModTools { get; }

    public SessionsViewModel Sessions { get; }

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private bool _isInitializing;

    /// <summary>Label of the update pill in the top bar; null hides it.</summary>
    [ObservableProperty]
    private string? _updateBadgeText;

    [ObservableProperty]
    private bool _isUpdateBusy;

    public MainWindowViewModel(
        ISettingsService settings,
        IGameDataService gameData,
        UpdateService updates,
        SetupViewModel setup,
        BrowserViewModel browser,
        PenumbraViewModel penumbra,
        FilesViewModel files,
        ModToolsViewModel modTools,
        SessionsViewModel sessions,
        ILogger<MainWindowViewModel> logger)
    {
        _settings = settings;
        _gameData = gameData;
        _updates = updates;
        Setup = setup;
        Browser = browser;
        Penumbra = penumbra;
        Files = files;
        ModTools = modTools;
        Sessions = sessions;
        _logger = logger;

        _currentView = setup;
        Setup.InstallationConfirmed += gameDir => _ = InitializeGameDataAsync(gameDir);
        Penumbra.AssetsChanged += () => _ = Browser.RefreshEffectiveAssetsAsync();
        Files.AssetsChanged += () => _ = Browser.RefreshEffectiveAssetsAsync();
        Sessions.AssetsChanged += () => _ = Browser.RefreshEffectiveAssetsAsync();
    }

    /// <summary>Called once at startup: revalidate the saved path and skip setup when it holds.</summary>
    public async Task StartAsync()
    {
        // Fire-and-forget: the update pill appears whenever the check finds
        // something, without ever delaying startup.
        _ = CheckForUpdatesAsync();

        Sessions.Initialize();

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

    private async Task CheckForUpdatesAsync()
    {
        var version = await _updates.CheckForUpdateAsync();
        if (version is not null)
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => UpdateBadgeText = $"Update v{version}");
    }

    /// <summary>Downloads the pending update, then applies it and restarts.</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (IsUpdateBusy)
            return;
        IsUpdateBusy = true;
        try
        {
            await _updates.DownloadAsync(percent =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => UpdateBadgeText = $"Downloading… {percent}%"));
            UpdateBadgeText = "Restarting…";
            _updates.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update failed");
            UpdateBadgeText = "Update failed";
            IsUpdateBusy = false;
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

            // Dev/testing hook: retarget a modpack headlessly and save the
            // result (runs first so AUTOIMPORT can pick up the output).
            var autoRetarget = Environment.GetEnvironmentVariable("MOONLACE_AUTORETARGET");
            if (!string.IsNullOrEmpty(autoRetarget))
                await ModTools.RetargetHeadlessAsync(autoRetarget);

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
