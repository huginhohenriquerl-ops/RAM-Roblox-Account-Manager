using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RAM.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public static class Paths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAM");

    public static string Accounts => Path.Combine(DataDir, "accounts.dat");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "ram.log");

    public static void Ensure() => Directory.CreateDirectory(DataDir);

    /// <summary>Write to a temp file and swap it in, so a crash mid-write never corrupts the original.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1024 * 1024) file.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}

/// <summary>Windows DPAPI (CurrentUser scope): only this Windows user on this PC can decrypt.</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

    static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        var input = Pin(data, out var inputHandle);
        var salt = Pin(entropy, out var saltHandle);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, "RAM", ref salt, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            inputHandle.Free();
            saltHandle.Free();
        }
    }

    static DataBlob Pin(byte[] bytes, out GCHandle handle)
    {
        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new DataBlob { Size = bytes.Length, Data = handle.AddrOfPinnedObject() };
    }
}
