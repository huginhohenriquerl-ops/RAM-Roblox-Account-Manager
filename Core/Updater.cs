using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace RAM.Core;

public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Failed, NotInstalled }

/// <summary>Auto-update from the GitHub releases of the project (Velopack packages).</summary>
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/huginhohenriquerl-ops/RAM-Roblox-Account-Manager";

    readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    UpdateInfo? _ready;
    Task? _running;

    public event EventHandler? Changed;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>False when running from a dev build instead of the installed copy.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>Checks GitHub and, if there is a newer release, downloads it in the background.</summary>
    public Task CheckAndDownloadAsync()
    {
        if (_running is { IsCompleted: false }) return _running;
        return _running = RunAsync();
    }

    async Task RunAsync()
    {
        if (!IsInstalled)
        {
            Set(UpdateState.NotInstalled);
            return;
        }
        if (_ready != null)
        {
            Set(UpdateState.Ready);
            return;
        }

        try
        {
            Set(UpdateState.Checking);
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null)
            {
                Set(UpdateState.UpToDate);
                return;
            }

            Progress = 0;
            Set(UpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(info, p =>
            {
                Progress = p;
                Changed?.Invoke(this, EventArgs.Empty);
            });
            _ready = info;
            Set(UpdateState.Ready);
            Log.Info($"update {info.TargetFullRelease.Version} downloaded");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Set(UpdateState.Failed);
            Log.Error("update", ex);
        }
    }

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_ready != null) _manager.ApplyUpdatesAndRestart(_ready);
    }

    void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
