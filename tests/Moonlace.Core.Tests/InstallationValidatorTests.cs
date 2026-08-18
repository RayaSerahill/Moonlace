using Moonlace.Core.Services;

namespace Moonlace.Core.Tests;

public sealed class InstallationValidatorTests : IDisposable
{
    private readonly string _root;

    public InstallationValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "moonlace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateInstall(string name = "FINAL FANTASY XIV - A Realm Reborn")
    {
        var gameDir = Path.Combine(_root, name, "game");
        var sqpackFfxiv = Path.Combine(gameDir, "sqpack", "ffxiv");
        Directory.CreateDirectory(sqpackFfxiv);
        File.WriteAllText(Path.Combine(sqpackFfxiv, "000000.win32.index"), "x");
        return gameDir;
    }

    [Fact]
    public void AcceptsGameDirectory()
    {
        var gameDir = CreateInstall();
        var result = InstallationValidator.Validate(gameDir);

        Assert.True(result.IsValid);
        Assert.Equal(gameDir, result.GameDirectory);
        Assert.Equal(Path.Combine(gameDir, "sqpack"), result.SqPackDirectory);
    }

    [Fact]
    public void AcceptsInstallRoot()
    {
        var gameDir = CreateInstall();
        var root = Path.GetDirectoryName(gameDir)!;

        var result = InstallationValidator.Validate(root);

        Assert.True(result.IsValid);
        Assert.Equal(gameDir, result.GameDirectory);
    }

    [Fact]
    public void AcceptsSqPackDirectory()
    {
        var gameDir = CreateInstall();
        var result = InstallationValidator.Validate(Path.Combine(gameDir, "sqpack"));

        Assert.True(result.IsValid);
        Assert.Equal(gameDir, result.GameDirectory);
    }

    [Fact]
    public void AcceptsSqPackFfxivDirectory()
    {
        var gameDir = CreateInstall();
        var result = InstallationValidator.Validate(Path.Combine(gameDir, "sqpack", "ffxiv"));

        Assert.True(result.IsValid);
        Assert.Equal(gameDir, result.GameDirectory);
    }

    [Fact]
    public void AcceptsBootSiblingDirectory()
    {
        var gameDir = CreateInstall();
        var boot = Path.Combine(Path.GetDirectoryName(gameDir)!, "boot");
        Directory.CreateDirectory(boot);

        var result = InstallationValidator.Validate(boot);

        Assert.True(result.IsValid);
        Assert.Equal(gameDir, result.GameDirectory);
    }

    [Fact]
    public void RejectsEmptyPath()
    {
        Assert.False(InstallationValidator.Validate("").IsValid);
        Assert.False(InstallationValidator.Validate(null).IsValid);
        Assert.False(InstallationValidator.Validate("   ").IsValid);
    }

    [Fact]
    public void RejectsMissingDirectory()
    {
        var result = InstallationValidator.Validate(Path.Combine(_root, "does-not-exist"));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RejectsDirectoryWithoutSqPack()
    {
        var dir = Path.Combine(_root, "random");
        Directory.CreateDirectory(dir);

        var result = InstallationValidator.Validate(dir);

        Assert.False(result.IsValid);
        Assert.Contains("FINAL FANTASY XIV", result.Error);
    }

    [Fact]
    public void RejectsSqPackWithoutIndexFiles()
    {
        var gameDir = Path.Combine(_root, "empty-install", "game");
        Directory.CreateDirectory(Path.Combine(gameDir, "sqpack", "ffxiv"));

        var result = InstallationValidator.Validate(gameDir);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ExecutableIsNotRequired()
    {
        // Deliberately no ffxiv_dx11.exe — Linux installs may not surface it.
        var gameDir = CreateInstall();
        Assert.True(InstallationValidator.Validate(gameDir).IsValid);
    }
}
