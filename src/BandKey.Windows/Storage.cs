using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BandKey.Core;

namespace BandKey.Windows;

public sealed record AppSettings
{
    public string? BleAddress { get; init; }
    public string? BleAddressType { get; init; }
    public bool AutoLock { get; init; }
    public bool BridgeEnabled { get; init; }
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 18443;
    public PresenceOptions Presence { get; init; } = new();
    public void Validate()
    {
        Presence.Validate();
        if (!System.Net.IPAddress.TryParse(ListenAddress, out _) || Port is < 1024 or > 65535)
            throw new ArgumentException("Bridge needs a literal local IP address and port 1024–65535");
        if (BleAddress != null && (!ulong.TryParse(BleAddress, System.Globalization.NumberStyles.HexNumber, null, out _) || BleAddress.Length != 12))
            throw new ArgumentException("BLE address must be 12 hex digits");
        if (BleAddressType != null && BleAddressType is not ("Public" or "Random" or "Unspecified"))
            throw new ArgumentException("Unsupported Bluetooth address type");
    }
}

public sealed class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; }
    private readonly object gate = new();
    public Store(string path) { DirectoryPath = Path.GetFullPath(path); Directory.CreateDirectory(DirectoryPath); }
    public T? Read<T>(string file) { lock (gate) { var p = Path.Combine(DirectoryPath, file); return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json) : default; } }
    public void Write<T>(string file, T value)
    {
        lock (gate)
        {
            var path = Path.Combine(DirectoryPath, file);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void Log(string eventName, string message)
    {
        lock (gate)
        {
            var path = Path.Combine(DirectoryPath, "bandkey.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.Move(path, path + ".1", true);
            // Callers pass event codes, never credentials, request bodies, key material or nonces.
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {eventName} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        }
    }
    public ECDsa LoadSigningKey()
    {
        var path = Path.Combine(DirectoryPath, "identity.dpapi");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            var raw = Dpapi.Unprotect(File.ReadAllBytes(path));
            try { key.ImportPkcs8PrivateKey(raw, out _); } finally { CryptographicOperations.ZeroMemory(raw); }
        }
        else
        {
            var raw = key.ExportPkcs8PrivateKey();
            try { File.WriteAllBytes(path, Dpapi.Protect(raw)); } finally { CryptographicOperations.ZeroMemory(raw); }
        }
        return key;
    }
    public X509Certificate2 LoadTlsCertificate()
    {
        var path = Path.Combine(DirectoryPath, "tls.dpapi");
        if (File.Exists(path))
        {
            var raw = Dpapi.Unprotect(File.ReadAllBytes(path));
            // Schannel needs a named Windows key. UserKeySet creates a temporary OS-protected
            // key container deleted on Dispose; this does not install or trust a certificate.
            try { return X509CertificateLoader.LoadPkcs12(raw, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=BandKey", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = cert.Export(X509ContentType.Pfx);
        try
        {
            File.WriteAllBytes(path, Dpapi.Protect(pfx));
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    public static byte[] Protect(byte[] input) => Transform(input, true);
    public static byte[] Unprotect(byte[] input) => Transform(input, false);
    private static byte[] Transform(byte[] input, bool protect)
    {
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var blob = new Blob { Length = input.Length, Data = pin.AddrOfPinnedObject() };
        Blob output = default;
        try
        {
            var ok = protect ? CryptProtectData(ref blob, "BandKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            pin.Free();
        }
    }
}
