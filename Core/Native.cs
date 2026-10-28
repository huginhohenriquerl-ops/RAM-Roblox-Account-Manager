using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RAM.Core;

internal static class Native
{
    // ntdll
    public const int SystemExtendedHandleInformation = 64;
    public const int ObjectNameInformation = 1;
    public static readonly int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public static readonly int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public static readonly int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    // kernel32
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_CLOSE_SOURCE = 0x1;
    public const uint DUPLICATE_SAME_ACCESS = 0x2;
    public const uint FILE_TYPE_DISK = 0x0001;

    // user32
    public const uint GW_OWNER = 4;
    public const int SW_RESTORE = 9;
    public const uint WM_SETTEXT = 0x000C;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // dwmapi
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    public static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder path, uint length, uint flags);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Top-level visible, unowned windows with a caption that belong to <paramref name="pid"/>.</summary>
    public static List<IntPtr> FindMainWindows(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out int owner);
            if (owner == pid && IsWindowVisible(hWnd) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero
                && GetWindowText(hWnd).Length > 0)
                found.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>WM_SETTEXT with a timeout so a hung Roblox window can never freeze our UI.</summary>
    public static void SetWindowTextSafe(IntPtr hWnd, string text) =>
        SendMessageTimeoutW(hWnd, WM_SETTEXT, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 250, out _);

    public static void FocusWindow(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }
}
