using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;

namespace Moonlace.Core.Settings;

/// <summary>
/// Persists <see cref="MoonlaceSettings"/> as JSON in the per-user application
/// configuration directory: ~/.config/Moonlace on Linux, %APPDATA%\Moonlace on Windows.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFile;

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, DefaultSettingsDirectory())
    {
    }

    /// <summary>Test hook: use an explicit settings directory.</summary>
    public SettingsService(ILogger<SettingsService> logger, string settingsDirectory)
    {
        _logger = logger;
        _settingsFile = Path.Combine(settingsDirectory, "settings.json");
    }

    private static string DefaultSettingsDirectory()
    {
        // ApplicationData maps to XDG_CONFIG_HOME (~/.config) on Linux and %APPDATA% on Windows.
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "Moonlace");
    }

    public MoonlaceSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var json = File.ReadAllText(_settingsFile);
                var settings = JsonSerializer.Deserialize<MoonlaceSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read settings from {File}; using defaults", _settingsFile);
        }

        return new MoonlaceSettings();
    }

    public void Save(MoonlaceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(settings, JsonOptions));
            _logger.LogInformation("Settings saved to {File}", _settingsFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {File}", _settingsFile);
        }
    }
}
