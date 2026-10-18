using System.Runtime.InteropServices;
using BandKey.Core;

namespace BandKey.Windows;

internal static class WindowsSession
{
    public const int WmSessionChange = 0x2B1, SessionLock = 7, SessionUnlock = 8;
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool LockWorkStation();

    public static bool? IsLocked()
    {
        // WTSINFOEX (level=1): DWORD level + 4-byte alignment + SessionId, State, SessionFlags.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, Environment.ProcessId == 0 ? -1 : System.Diagnostics.Process.GetCurrentProcess().SessionId, 25, out var buffer, out var size)) return null;
        try
        {
            if (size < 20 || Marshal.ReadInt32(buffer) != 1) return null;
            return Marshal.ReadInt32(buffer, 16) switch { 0 => true, 1 => false, _ => null };
        }
        finally { WTSFreeMemory(buffer); }
    }
}

public sealed class WindowsSessionController : IInteractiveSession
{
    public bool IsInteractive => Environment.UserInteractive && System.Diagnostics.Process.GetCurrentProcess().SessionId != 0;
    public bool? IsLocked() => WindowsSession.IsLocked();
    public LockRequestResult RequestLock()
    {
        if (!IsInteractive) return new(false, "non-interactive-session");
        return WindowsSession.LockWorkStation() ? new(true, "submitted") : new(false, "win32-" + Marshal.GetLastWin32Error());
    }
}
