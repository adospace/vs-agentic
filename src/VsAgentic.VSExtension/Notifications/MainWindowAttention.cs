using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VsAgentic.VSExtension.Notifications;

/// <summary>
/// The Win32 side of calling the user back to this Visual Studio: whether it is
/// the window they work in, the taskbar flash, and bringing it forward.
/// </summary>
internal static class MainWindowAttention
{
    /// <summary>
    /// Whether the window the user works in belongs to this Visual Studio. Any
    /// of its windows counts, so a floating chat or tool window does as well as
    /// the main one. A Visual Studio on another virtual desktop is never in
    /// front, since the foreground window is always on the current desktop.
    /// </summary>
    public static bool IsInForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)CurrentProcessId;
    }

    /// <summary>
    /// Flashes the taskbar button until the window comes to the front, or stops
    /// a flash that is running. On another virtual desktop the button is only
    /// visible when the taskbar shows the windows of every desktop.
    /// </summary>
    public static void Flash(bool on)
    {
        var hwnd = MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = on ? FLASHW_TRAY | FLASHW_TIMERNOFG : FLASHW_STOP,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>
    /// Brings the main window to the front, restoring it if it is minimized.
    /// Windows switches to the window's virtual desktop. Called after a toast
    /// click, which gives this process the right to take the foreground.
    /// </summary>
    public static void BringToFront()
    {
        var hwnd = MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private static IntPtr MainWindowHandle =>
        Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// The AppUserModelID this process shows under on the taskbar, if Visual
    /// Studio set one explicitly.
    /// </summary>
    public static string? GetProcessAppId() =>
        GetCurrentProcessExplicitAppUserModelID(out var id) == 0 && !string.IsNullOrEmpty(id) ? id : null;

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] out string appId);
}
