namespace Moonlace.Core.Penumbra;

public static class ModPaths
{
    /// <summary>
    /// Resolves a mod-relative path to the actual on-disk path, matching each
    /// segment case-insensitively: Penumbra runs on case-insensitive
    /// filesystems, so mod JSON casing routinely differs from the real
    /// directory names. Missing segments fall back to the literal spelling
    /// (for files that are about to be created).
    /// </summary>
    public static string ResolveCaseInsensitive(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var literal = Path.Combine(current, segment);
            if (File.Exists(literal) || Directory.Exists(literal))
            {
                current = literal;
                continue;
            }

            string? match = null;
            if (Directory.Exists(current))
            {
                match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase));
            }

            current = match ?? literal;
        }

        return current;
    }

    private static readonly string[] GameRoots =
        ["chara/", "bg/", "bgcommon/", "ui/", "vfx/", "sound/", "music/", "cut/", "common/", "shader/"];

    /// <summary>
    /// True when a redirection key looks like a real game path. Penumbra
    /// keeps a mod's unused files under pseudo-keys ("Option Name/chara/…")
    /// that never match a game request — those are not editable content.
    /// </summary>
    public static bool LooksLikeGamePath(string gamePath) =>
        GameRoots.Any(root => gamePath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
}
