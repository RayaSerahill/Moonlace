namespace Moonlace.Core.Settings;

/// <summary>How long unused editing sessions are kept before being deleted to free disk space.</summary>
public enum SessionRetention
{
    Forever = 0,
    OneDay = 1,
    OneWeek = 2,
    OneMonth = 3,
}

public static class SessionRetentionExtensions
{
    /// <summary>The age at which an unused session expires, or null when sessions are kept forever.</summary>
    public static TimeSpan? ToMaxAge(this SessionRetention retention) => retention switch
    {
        SessionRetention.OneDay => TimeSpan.FromDays(1),
        SessionRetention.OneWeek => TimeSpan.FromDays(7),
        SessionRetention.OneMonth => TimeSpan.FromDays(30),
        _ => null,
    };
}

public sealed class MoonlaceSettings
{
    /// <summary>User-selected FFXIV game directory (the directory containing "sqpack").</summary>
    public string? GamePath { get; set; }

    /// <summary>How long unused editing sessions are kept (serialized as an int; appended values only).</summary>
    public SessionRetention SessionRetention { get; set; } = SessionRetention.Forever;
}
