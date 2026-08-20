using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Session;
using Moonlace.Core.Settings;

namespace Moonlace.App.ViewModels;

/// <summary>One row of the "Session files" panel.</summary>
public sealed record TouchedFileRow(string ItemName, string GamePath, string Kind);

/// <summary>One previous session in the "Connect to previous" panel.</summary>
public sealed record SessionChoice(string Id, string Title, string Detail);

/// <summary>
/// The Sessions menu in the top bar. Every launch begins a fresh editing
/// session; from here the user can clear the worktree by starting another
/// one, inspect which files the current session has touched, reconnect to a
/// previous session, and choose how long unused sessions are kept before
/// they are deleted to free disk space.
/// </summary>
public partial class SessionsViewModel : ViewModelBase
{
    private readonly ISessionService _session;
    private readonly ISettingsService _settings;
    private readonly ILogger<SessionsViewModel> _logger;

    /// <summary>Raised after the current session changed wholesale (new or connect), so the viewport and tabs reload.</summary>
    public event Action? AssetsChanged;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isFilesPanelOpen;

    [ObservableProperty]
    private string _filesSummary = "";

    public ObservableCollection<TouchedFileRow> TouchedFiles { get; } = [];

    [ObservableProperty]
    private bool _isConnectPanelOpen;

    [ObservableProperty]
    private bool _hasPreviousSessions;

    [ObservableProperty]
    private SessionChoice? _selectedSession;

    public ObservableCollection<SessionChoice> PreviousSessions { get; } = [];

    [ObservableProperty]
    private bool _isRetentionDay;

    [ObservableProperty]
    private bool _isRetentionWeek;

    [ObservableProperty]
    private bool _isRetentionMonth;

    [ObservableProperty]
    private bool _isRetentionForever = true;

    public SessionsViewModel(
        ISessionService session,
        ISettingsService settings,
        ILogger<SessionsViewModel> logger)
    {
        _session = session;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Called once at startup: reflects the saved retention choice and prunes
    /// sessions that expired while Moonlace was closed.
    /// </summary>
    public void Initialize()
    {
        var retention = _settings.Load().SessionRetention;
        SetRetentionFlags(retention);
        PruneInBackground(retention);
    }

    [RelayCommand]
    private void NewSession()
    {
        ErrorText = null;
        _session.StartNewSession();
        AssetsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task OpenFilesPanelAsync()
    {
        ErrorText = null;
        var touched = await Task.Run(() => _session.GetTouchedAssets());

        TouchedFiles.Clear();
        foreach (var asset in touched)
            TouchedFiles.Add(new TouchedFileRow(asset.ItemName, asset.GamePath, asset.Kind.ToString()));

        var itemCount = touched.Select(a => a.ItemRowId).Distinct().Count();
        FilesSummary = touched.Count == 0
            ? "This session has not modified anything yet."
            : $"{touched.Count} modified file{(touched.Count == 1 ? "" : "s")} across " +
              $"{itemCount} item{(itemCount == 1 ? "" : "s")}.";
        IsFilesPanelOpen = true;
    }

    [RelayCommand]
    private void CloseFilesPanel() => IsFilesPanelOpen = false;

    [RelayCommand]
    private async Task OpenConnectPanelAsync()
    {
        ErrorText = null;
        var sessions = await Task.Run(() => _session.ListPreviousSessions());

        PreviousSessions.Clear();
        foreach (var info in sessions)
        {
            var items = string.Join(", ", info.ItemNames);
            var detail = $"{info.FileCount} file{(info.FileCount == 1 ? "" : "s")}, " +
                         $"{FormatBytes(info.TotalBytes)} · {items}";
            PreviousSessions.Add(new SessionChoice(
                info.Id,
                $"Session from {info.LastUsedAtUtc.ToLocalTime():g}",
                detail));
        }

        HasPreviousSessions = PreviousSessions.Count > 0;
        SelectedSession = PreviousSessions.FirstOrDefault();
        IsConnectPanelOpen = true;
    }

    [RelayCommand]
    private void CancelConnect() => IsConnectPanelOpen = false;

    [RelayCommand]
    private void Connect()
    {
        if (SelectedSession is null)
            return;

        try
        {
            _session.ConnectToSession(SelectedSession.Id);
            IsConnectPanelOpen = false;
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to session {Id}", SelectedSession.Id);
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void SetRetention(string key)
    {
        var retention = key switch
        {
            "day" => SessionRetention.OneDay,
            "week" => SessionRetention.OneWeek,
            "month" => SessionRetention.OneMonth,
            _ => SessionRetention.Forever,
        };

        var settings = _settings.Load();
        settings.SessionRetention = retention;
        _settings.Save(settings);
        SetRetentionFlags(retention);

        // Apply the tighter policy right away so the space is freed now, not
        // on the next launch.
        PruneInBackground(retention);
    }

    private void SetRetentionFlags(SessionRetention retention)
    {
        IsRetentionDay = retention == SessionRetention.OneDay;
        IsRetentionWeek = retention == SessionRetention.OneWeek;
        IsRetentionMonth = retention == SessionRetention.OneMonth;
        IsRetentionForever = retention == SessionRetention.Forever;
    }

    private void PruneInBackground(SessionRetention retention)
    {
        if (retention.ToMaxAge() is not { } maxAge)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                _session.PruneExpiredSessions(maxAge);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session pruning failed");
            }
        });
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
