using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Moonlace.App.Services;

/// <summary>
/// Checks the GitHub releases of the Moonlace repository for a newer Velopack
/// build, downloads it, and applies it with a restart. When the running app is
/// not a Velopack install (dev builds, plain unpacked archives) every member
/// is a safe no-op so callers never need to special-case that.
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/RayaSerahill/Moonlace";

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;

        // Dev/testing hook: point the updater at a local vpk output directory
        // or any http(s) feed instead of the GitHub releases.
        var source = Environment.GetEnvironmentVariable("MOONLACE_UPDATE_SOURCE");
        _manager = string.IsNullOrEmpty(source)
            ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
            : new UpdateManager(source);
    }

    /// <summary>False for dev builds and plain archives; only Velopack installs can update.</summary>
    public bool IsSupported => _manager.IsInstalled;

    /// <summary>
    /// Checks GitHub for a newer release. Returns its version string, or null
    /// when already up to date, unsupported, or the check failed (offline,
    /// GitHub rate limit). Failures are logged, never thrown: an update check
    /// must not disturb startup.
    /// </summary>
    public async Task<string?> CheckForUpdateAsync()
    {
        if (!IsSupported)
        {
            _logger.LogInformation("Not a Velopack install; skipping update check");
            return null;
        }

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            if (_pending is null)
            {
                _logger.LogInformation("Up to date (v{Version})", _manager.CurrentVersion);
                return null;
            }

            var version = _pending.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update available: v{Version}", version);
            return version;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>
    /// Downloads the update found by <see cref="CheckForUpdateAsync"/>.
    /// Progress is reported as 0..100.
    /// </summary>
    public async Task DownloadAsync(Action<int>? progress = null)
    {
        if (_pending is null)
            throw new InvalidOperationException("No pending update; call CheckForUpdateAsync first.");
        await _manager.DownloadUpdatesAsync(_pending, progress);
    }

    /// <summary>Applies the downloaded update and restarts the app. Does not return.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null)
            throw new InvalidOperationException("No pending update; call CheckForUpdateAsync first.");
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
