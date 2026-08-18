using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Settings;

namespace Moonlace.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonlace-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SettingsService Create() => new(NullLogger<SettingsService>.Instance, _dir);

    [Fact]
    public void LoadReturnsDefaultsWhenNoFileExists()
    {
        var settings = Create().Load();
        Assert.Null(settings.GamePath);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var service = Create();
        service.Save(new MoonlaceSettings { GamePath = "/some/game/path" });

        var loaded = Create().Load();
        Assert.Equal("/some/game/path", loaded.GamePath);
    }

    [Fact]
    public void LoadSurvivesCorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json !!");

        var settings = Create().Load();
        Assert.Null(settings.GamePath);
    }
}
