using System.Runtime.InteropServices;

namespace BandKey.Windows;

public sealed record ClassicDeviceObservation(string Address, string Name, bool ConnectedToPc,
    bool RememberedByPc, bool AuthenticatedWithPc, string? LastSeenSystemTime);
public sealed record ClassicProbeReport(DateTimeOffset GeneratedAt, bool InquiryIssued, int NativeError,
    ClassicDeviceObservation[] Devices, bool PairingAttempted = false);

/// <summary>Classic Bluetooth discovery only. Never pairs, changes services, or opens RFCOMM.</summary>
public static class ClassicProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParams
    {
        public uint Size;
        public int Authenticated, Remembered, Unknown, Connected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        public readonly string? Iso()
        {
            // Preserve the native wall-clock fields; do not infer scan freshness or a time zone.
            try { return Year == 0 ? null : new DateTime(Year, Month, Day, Hour, Minute, Second, Milliseconds, DateTimeKind.Unspecified).ToString("yyyy-MM-ddTHH:mm:ss.fff"); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint Class;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams parameters, ref DeviceInfo device);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr search, ref DeviceInfo device);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr search);

    public static ClassicProbeReport Scan(bool inquiry, int seconds = 8)
    {
        var parameters = new SearchParams { Size = (uint)Marshal.SizeOf<SearchParams>(), Authenticated = 1,
            Remembered = 1, Unknown = 1, Connected = 1, IssueInquiry = inquiry ? 1 : 0,
            TimeoutMultiplier = (byte)Math.Clamp((int)Math.Ceiling(seconds / 1.28), 1, 12) };
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = "" };
        var list = new List<ClassicDeviceObservation>();
        var search = BluetoothFindFirstDevice(ref parameters, ref info);
        if (search == IntPtr.Zero) return new(DateTimeOffset.UtcNow, inquiry, Marshal.GetLastWin32Error(), []);
        var error = 0;
        try
        {
            do
            {
                list.Add(new(info.Address.ToString("X12"), info.Name, info.Connected != 0, info.Remembered != 0,
                    info.Authenticated != 0, info.LastSeen.Iso()));
                info.Size = (uint)Marshal.SizeOf<DeviceInfo>();
            } while (BluetoothFindNextDevice(search, ref info));
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 259) error = lastError;
        }
        finally { BluetoothFindDeviceClose(search); }
        return new(DateTimeOffset.UtcNow, inquiry, error, list.ToArray());
    }
}
