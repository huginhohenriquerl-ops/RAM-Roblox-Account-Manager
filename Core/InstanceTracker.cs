using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace RAM.Core;

public sealed class RobloxInstance : ObservableObject
{
    Account? _account;
    string _details = "";

    public RobloxInstance(int pid, DateTime? startTime)
    {
        Pid = pid;
        StartTime = startTime;
    }

    public int Pid { get; }
    public DateTime? StartTime { get; }

    public Account? Account
    {
        get => _account;
        set
        {
            if (Set(ref _account, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Initial));
                OnPropertyChanged(nameof(AvatarUrl));
            }
        }
    }

    public string Title => Account?.Title ?? "Conta não identificada";
    public string Initial => Account?.Initial ?? "?";
    public string? AvatarUrl => Account?.AvatarUrl;

    public string Details
    {
        get => _details;
        set => Set(ref _details, value);
    }
}

/// <summary>
/// Polls RobloxPlayerBeta processes, attributes new ones to the account that was just launched and keeps
/// window titles labelled with the account name.
/// </summary>
public sealed class InstanceTracker
{
    sealed class PendingLaunch
    {
        public required Account Account { get; init; }
        public required HashSet<int> KnownPids { get; init; }
        public DateTime CreatedAt { get; } = DateTime.Now;
        public int? AssignedPid { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    readonly ObservableCollection<Account> _accounts;
    readonly List<PendingLaunch> _pending = new();
    readonly DispatcherTimer _timer;

    public ObservableCollection<RobloxInstance> Instances { get; } = new();
    public Func<bool> RenameWindows { get; set; } = () => false;
    public event EventHandler? Changed;

    public InstanceTracker(ObservableCollection<Account> accounts)
    {
        _accounts = accounts;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    /// <summary>Call right before starting Roblox for <paramref name="account"/>.</summary>
    public void ExpectLaunch(Account account, HashSet<int> pidsBeforeLaunch)
    {
        _pending.Add(new PendingLaunch { Account = account, KnownPids = pidsBeforeLaunch });
        _timer.Interval = TimeSpan.FromSeconds(0.75);
    }

    public void Refresh()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(RobloxProcesses.ProcessName); }
        catch (Exception ex) { Log.Error("enumerate processes", ex); return; }

        bool changed = false;
        var alive = new HashSet<int>();
        try
        {
            foreach (var p in processes.OrderBy(SafeStart))
            {
                alive.Add(p.Id);
                var instance = Instances.FirstOrDefault(i => i.Pid == p.Id);
                if (instance == null)
                {
                    instance = new RobloxInstance(p.Id, SafeStart(p));
                    var pending = _pending.FirstOrDefault(x => x.AssignedPid == null && !x.KnownPids.Contains(p.Id));
                    if (pending != null)
                    {
                        pending.AssignedPid = p.Id;
                        pending.AssignedAt = DateTime.Now;
                        instance.Account = pending.Account;
                    }
                    Instances.Add(instance);
                    changed = true;
                }
                instance.Details = Describe(p, instance);
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        for (int i = Instances.Count - 1; i >= 0; i--)
        {
            if (alive.Contains(Instances[i].Pid)) continue;
            int pid = Instances[i].Pid;
            Instances.RemoveAt(i);
            changed = true;
            // Bootstrapper/launcher processes exit quickly and hand over to the real client: re-arm the pending
            // launch so the next new process gets the account instead.
            foreach (var p in _pending.Where(x => x.AssignedPid == pid && (DateTime.Now - x.AssignedAt).TotalSeconds < 30))
                p.AssignedPid = null;
        }

        _pending.RemoveAll(p =>
            (p.AssignedPid != null && (DateTime.Now - p.AssignedAt).TotalSeconds > 30) ||
            (DateTime.Now - p.CreatedAt).TotalSeconds > 120);
        if (_pending.Count == 0) _timer.Interval = TimeSpan.FromSeconds(1.5);

        foreach (var account in _accounts)
            account.RunningCount = Instances.Count(i => i.Account == account);

        if (RenameWindows()) LabelWindows();
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    void LabelWindows()
    {
        foreach (var instance in Instances.Where(i => i.Account != null))
        {
            var desired = "Roblox — " + instance.Account!.Title;
            foreach (var hWnd in Native.FindMainWindows(instance.Pid))
                if (Native.GetWindowText(hWnd) != desired)
                    Native.SetWindowTextSafe(hWnd, desired);
        }
    }

    public static void Focus(int pid)
    {
        var hWnd = Native.FindMainWindows(pid).FirstOrDefault();
        if (hWnd != IntPtr.Zero) Native.FocusWindow(hWnd);
    }

    static DateTime? SafeStart(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    static string Describe(Process p, RobloxInstance instance)
    {
        var parts = new List<string> { "PID " + p.Id };
        if (instance.StartTime is { } start)
        {
            var up = DateTime.Now - start;
            parts.Add(up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:00}min" : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes} min" : "agora");
        }
        try { parts.Add($"{p.WorkingSet64 / (1024 * 1024)} MB"); } catch { }
        return string.Join("  ·  ", parts);
    }
}
