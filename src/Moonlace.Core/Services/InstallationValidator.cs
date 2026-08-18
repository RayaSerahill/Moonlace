namespace Moonlace.Core.Services;

/// <summary>
/// Locates and validates an FFXIV installation from a user-chosen directory.
/// Pure logic — no UI, no Lumina — so it is fully unit-testable.
/// </summary>
public static class InstallationValidator
{
    /// <summary>
    /// Tries to resolve a usable FFXIV "game" directory from <paramref name="userPath"/>.
    /// Accepts the game root, the "game" directory itself, the "sqpack" directory,
    /// or a parent that contains any of these.
    /// </summary>
    /// <returns>Result whose <see cref="ValidationResult.GameDirectory"/> is the directory
    /// containing "sqpack" (i.e. the "game" directory) when valid.</returns>
    public static ValidationResult Validate(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            return ValidationResult.Failure("No directory selected.");

        string full;
        try
        {
            full = Path.GetFullPath(userPath);
        }
        catch (Exception)
        {
            return ValidationResult.Failure("The selected path is not a valid directory path.");
        }

        if (!Directory.Exists(full))
            return ValidationResult.Failure("The selected directory does not exist.");

        foreach (var candidate in EnumerateCandidates(full))
        {
            if (IsGameDirectory(candidate))
                return ValidationResult.Success(candidate);
        }

        return ValidationResult.Failure(
            "This does not look like a FINAL FANTASY XIV installation. " +
            "Expected to find a \"game\" directory containing \"sqpack/ffxiv\".");
    }

    private static IEnumerable<string> EnumerateCandidates(string path)
    {
        // The path itself may be the game dir.
        yield return path;

        // The path may be the game root (or an obvious directory near it).
        yield return Path.Combine(path, "game");

        // The user may have picked the sqpack dir or the sqpack/ffxiv dir.
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null)
            yield break;

        if (string.Equals(name, "sqpack", StringComparison.OrdinalIgnoreCase))
            yield return parent;
        else if (string.Equals(name, "ffxiv", StringComparison.OrdinalIgnoreCase))
        {
            var grandParent = Path.GetDirectoryName(parent);
            if (grandParent is not null)
                yield return grandParent;
        }
        else if (string.Equals(name, "boot", StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(parent, "game");
    }

    /// <summary>
    /// A directory is a usable game directory when it contains the SqPack data
    /// for the base game: sqpack/ffxiv with at least one index file. The
    /// executable is deliberately not required (Linux installs may differ).
    /// </summary>
    private static bool IsGameDirectory(string dir)
    {
        var sqpackFfxiv = Path.Combine(dir, "sqpack", "ffxiv");
        if (!Directory.Exists(sqpackFfxiv))
            return false;

        try
        {
            return Directory.EnumerateFiles(sqpackFfxiv, "*.index").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record ValidationResult(bool IsValid, string? GameDirectory, string? Error)
{
    public static ValidationResult Success(string gameDirectory) => new(true, gameDirectory, null);

    public static ValidationResult Failure(string error) => new(false, null, error);

    /// <summary>The sqpack directory beneath the resolved game directory.</summary>
    public string? SqPackDirectory => GameDirectory is null ? null : Path.Combine(GameDirectory, "sqpack");
}
