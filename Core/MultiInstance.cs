using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RAM.Core;

public static class RobloxProcesses
{
    public const string ProcessName = "RobloxPlayerBeta";

    public static List<int> GetPids()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        var pids = processes.Select(p => p.Id).ToList();
        foreach (var p in processes) p.Dispose();
        return pids;
    }

    public static bool AnyRunning() => GetPids().Count > 0;

    /// <summary>Asks every Roblox window to close, then kills whatever is left after the timeout.</summary>
    public static Task CloseAsync(int pid, int timeoutMs = 4000) => Task.Run(() =>
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.CloseMainWindow();
            if (!p.WaitForExit(timeoutMs)) p.Kill();
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }
        catch (Exception ex) { Log.Error($"close pid {pid}", ex); }
    });

    public static Task CloseAllAsync() => Task.WhenAll(GetPids().Select(pid => CloseAsync(pid)));
}

public sealed class HandleScanResult
{
    public int Processes { get; set; }
    public int HandlesInspected { get; set; }
    public int SingletonsClosed { get; set; }
    public int CookieLocksClosed { get; set; }
    public bool TypeIndexResolved { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Closes the singleton objects (and optionally the cookie file lock) inside running Roblox clients.
/// Reimplementation of MultiBloxy's HandleCloser.CloseAllHandles with the fixes listed in docs/MultiBloxy-RE.md.
/// </summary>
public static class RobloxHandleCloser
{
    public static readonly string[] SingletonNames = { "ROBLOX_singletonEvent", "ROBLOX_singletonMutex" };
    const string CookieFileName = "RobloxCookies.dat";

    // Access masks of handles that are known to hang NtQueryObject (synchronous pipes). Only used as a fallback
    // when the object type index could not be resolved.
    static readonly HashSet<uint> HangingAccessMasks = new() { 0x0012019F, 0x001A019F, 0x00120189, 0x00100000, 0x0012008D };

    readonly struct HandleEntry
    {
        public readonly int Pid;
        public readonly IntPtr Handle;
        public readonly uint Access;
        public readonly ushort TypeIndex;

        public HandleEntry(int pid, IntPtr handle, uint access, ushort typeIndex)
        {
            Pid = pid; Handle = handle; Access = access; TypeIndex = typeIndex;
        }
    }

    public static HandleScanResult Run(bool singletons, bool cookieLock)
    {
        var sw = Stopwatch.StartNew();
        var result = new HandleScanResult();
        var pids = RobloxProcesses.GetPids().ToHashSet();
        result.Processes = pids.Count;
        if (pids.Count == 0 || (!singletons && !cookieLock)) return result;

        // Probe objects of known types: their entries in the handle table tell us the ObjectTypeIndex of
        // Event / Mutant / File on this Windows build, so we only ever query names of safe handle types.
        using var probeEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var probeMutex = new Mutex(false);
        using var probeFile = new FileStream(Path.Combine(Path.GetTempPath(), $"ram-probe-{Environment.ProcessId}.tmp"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        int self = Environment.ProcessId;
        IntPtr eventHandle = probeEvent.SafeWaitHandle.DangerousGetHandle();
        IntPtr mutexHandle = probeMutex.SafeWaitHandle.DangerousGetHandle();
        IntPtr fileHandle = probeFile.SafeFileHandle.DangerousGetHandle();

        List<HandleEntry> entries;
        int eventType = -1, mutantType = -1, fileType = -1;
        try
        {
            entries = SnapshotHandles(e =>
            {
                if (e.Pid == self)
                {
                    if (e.Handle == eventHandle) eventType = e.TypeIndex;
                    else if (e.Handle == mutexHandle) mutantType = e.TypeIndex;
                    else if (e.Handle == fileHandle) fileType = e.TypeIndex;
                    return false;
                }
                return pids.Contains(e.Pid);
            });
        }
        catch (Exception ex)
        {
            result.Errors.Add("Falha ao ler a tabela de handles: " + ex.Message);
            return result;
        }

        result.TypeIndexResolved = eventType >= 0 && mutantType >= 0;
        var processHandles = new Dictionary<int, IntPtr>();
        try
        {
            foreach (var entry in entries)
            {
                bool syncType = result.TypeIndexResolved
                    ? entry.TypeIndex == eventType || entry.TypeIndex == mutantType
                    : !HangingAccessMasks.Contains(entry.Access);
                bool fileCandidate = cookieLock && fileType >= 0 && entry.TypeIndex == fileType;
                if (!(singletons && syncType) && !fileCandidate) continue;

                if (!processHandles.TryGetValue(entry.Pid, out var process))
                {
                    process = Native.OpenProcess(Native.PROCESS_DUP_HANDLE, false, entry.Pid);
                    processHandles[entry.Pid] = process;
                    if (process == IntPtr.Zero)
                        result.Errors.Add($"Acesso negado ao processo {entry.Pid} (erro {Marshal.GetLastWin32Error()})");
                }
                if (process == IntPtr.Zero) continue;

                if (!Native.DuplicateHandle(process, entry.Handle, Native.GetCurrentProcess(), out var local, 0, false,
                        Native.DUPLICATE_SAME_ACCESS))
                    continue;

                result.HandlesInspected++;
                bool close = false;
                bool leak = false;
                bool isCookie = false;
                try
                {
                    if (fileCandidate)
                    {
                        // GetFileType never blocks; only resolve paths of real disk files.
                        if (Native.GetFileType(local) == Native.FILE_TYPE_DISK)
                        {
                            var path = GetFinalPath(local);
                            isCookie = close = path != null &&
                                path.EndsWith("\\" + CookieFileName, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    else
                    {
                        // Without a resolved type index we may be looking at a pipe, where NtQueryObject can block forever.
                        string? name;
                        if (result.TypeIndexResolved) name = QueryObjectName(local);
                        else leak = !QueryObjectNameWithTimeout(local, out name);
                        close = name != null && SingletonNames.Any(n =>
                            name.EndsWith("\\" + n, StringComparison.Ordinal));
                    }
                }
                finally
                {
                    if (!leak) Native.CloseHandle(local);
                }

                if (!close) continue;
                if (Native.DuplicateHandle(process, entry.Handle, IntPtr.Zero, out _, 0, false, Native.DUPLICATE_CLOSE_SOURCE))
                {
                    if (isCookie) result.CookieLocksClosed++;
                    else result.SingletonsClosed++;
                }
                else
                {
                    result.Errors.Add($"Não foi possível fechar o handle 0x{entry.Handle.ToInt64():X} do PID {entry.Pid}");
                }
            }
        }
        finally
        {
            foreach (var h in processHandles.Values)
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            result.Elapsed = sw.Elapsed;
        }

        Log.Info($"handle scan: procs={result.Processes} inspected={result.HandlesInspected} " +
                 $"singletons={result.SingletonsClosed} cookies={result.CookieLocksClosed} typed={result.TypeIndexResolved} " +
                 $"in {result.Elapsed.TotalMilliseconds:0}ms");
        return result;
    }

    /// <summary>
    /// NtQuerySystemInformation(SystemExtendedHandleInformation). Uses the extended layout so PIDs and handle
    /// values are full pointer width (MultiBloxy used class 16, which truncates both to 16 bits).
    /// </summary>
    static unsafe List<HandleEntry> SnapshotHandles(Func<HandleEntry, bool> keep)
    {
        int length = 4 * 1024 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            while (true)
            {
                int status = Native.NtQuerySystemInformation(Native.SystemExtendedHandleInformation, buffer, length, out int needed);
                if (status == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    length = Math.Max(length * 2, needed + 256 * 1024);
                    buffer = Marshal.AllocHGlobal(length);
                    continue;
                }
                if (status != 0) throw new InvalidOperationException($"NTSTATUS 0x{status:X8}");
                break;
            }

            // SYSTEM_HANDLE_INFORMATION_EX { ULONG_PTR NumberOfHandles; ULONG_PTR Reserved; ENTRY Handles[]; }
            // ENTRY { PVOID Object; ULONG_PTR Pid; ULONG_PTR Handle; ULONG Access; USHORT BackTrace; USHORT TypeIndex; ULONG Attributes; ULONG Reserved; }
            int ptr = IntPtr.Size;
            int entrySize = ptr * 3 + 16;
            byte* basePtr = (byte*)buffer;
            long count = (long)*(nuint*)basePtr;
            byte* first = basePtr + ptr * 2;
            var list = new List<HandleEntry>();
            for (long i = 0; i < count; i++)
            {
                byte* e = first + i * entrySize;
                var entry = new HandleEntry(
                    (int)*(nuint*)(e + ptr),
                    (IntPtr)(nint)(*(nuint*)(e + ptr * 2)),
                    *(uint*)(e + ptr * 3),
                    *(ushort*)(e + ptr * 3 + 6));
                if (keep(entry)) list.Add(entry);
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static string? QueryObjectName(IntPtr handle)
    {
        int length = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            int status = Native.NtQueryObject(handle, Native.ObjectNameInformation, buffer, length, out int needed);
            if (status == Native.STATUS_INFO_LENGTH_MISMATCH || status == Native.STATUS_BUFFER_OVERFLOW ||
                status == Native.STATUS_BUFFER_TOO_SMALL)
            {
                Marshal.FreeHGlobal(buffer);
                length = Math.Max(needed, length * 2);
                buffer = Marshal.AllocHGlobal(length);
                status = Native.NtQueryObject(handle, Native.ObjectNameInformation, buffer, length, out _);
            }
            if (status != 0) return null;

            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
            int bytes = (ushort)Marshal.ReadInt16(buffer);
            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return bytes == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <returns>false when the query timed out; the worker is then abandoned and still owns the handle.</returns>
    static bool QueryObjectNameWithTimeout(IntPtr handle, out string? name, int timeoutMs = 150)
    {
        string? result = null;
        var worker = new Thread(() => result = QueryObjectName(handle)) { IsBackground = true, Name = "ntqueryobject" };
        worker.Start();
        bool done = worker.Join(timeoutMs);
        name = done ? result : null;
        return done;
    }

    static string? GetFinalPath(IntPtr handle)
    {
        var sb = new StringBuilder(1024);
        uint len = Native.GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        return len == 0 || len >= sb.Capacity ? null : sb.ToString();
    }
}

/// <summary>
/// Keeps Roblox's singleton names occupied so every new client believes it is the only one running.
/// Must be used from the UI thread: mutex ownership is thread-affine and the UI thread lives as long as the app.
/// </summary>
public sealed class MultiInstanceManager : IDisposable
{
    const string EventName = "ROBLOX_singletonEvent";
    const string LegacyMutexName = "ROBLOX_singletonMutex";

    Mutex? _eventSquatter;
    Mutex? _legacyMutex;

    public event EventHandler? StateChanged;

    public bool IsActive => _eventSquatter != null;
    public bool LegacyHeld => _legacyMutex != null;
    public string? LastError { get; private set; }
    public HandleScanResult? LastScan { get; private set; }

    Task<bool>? _enabling;

    /// <summary>Concurrent callers (startup, tray, switch) share the same attempt.</summary>
    public Task<bool> EnableAsync(bool holdLegacyMutex, bool releaseCookieLock)
    {
        if (_enabling is { IsCompleted: false }) return _enabling;
        return _enabling = EnableCoreAsync(holdLegacyMutex, releaseCookieLock);
    }

    async Task<bool> EnableCoreAsync(bool holdLegacyMutex, bool releaseCookieLock)
    {
        LastError = null;
        bool ok = TryHoldAll(holdLegacyMutex);
        if ((!ok || (holdLegacyMutex && !LegacyHeld)) && RobloxProcesses.AnyRunning())
        {
            // Roblox already registered the names: close them inside the running clients and retry.
            LastScan = await Task.Run(() => RobloxHandleCloser.Run(true, releaseCookieLock));
            ok = TryHoldAll(holdLegacyMutex);
            if (!ok && LastScan.Errors.Count > 0) LastError = LastScan.Errors[0];
        }
        if (!ok) LastError ??= "O nome ROBLOX_singletonEvent está em uso e não pôde ser liberado.";
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public void Disable()
    {
        Release(ref _eventSquatter);
        Release(ref _legacyMutex);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    bool TryHoldAll(bool holdLegacyMutex)
    {
        _eventSquatter ??= TryHold(EventName);
        if (holdLegacyMutex) _legacyMutex ??= TryHold(LegacyMutexName);
        else Release(ref _legacyMutex);
        return _eventSquatter != null;
    }

    Mutex? TryHold(string name)
    {
        try
        {
            var mutex = new Mutex(true, name, out bool createdNew);
            if (createdNew) return mutex;

            // The name exists as a mutex owned by someone else (Roblox): only keep it if we can take ownership.
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (owned) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Name taken by an object of another type — Roblox's singleton Event.
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    static void Release(ref Mutex? mutex)
    {
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { }
        mutex.Dispose();
        mutex = null;
    }

    public void Dispose()
    {
        Release(ref _eventSquatter);
        Release(ref _legacyMutex);
    }
}
