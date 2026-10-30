using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using BandKey.Core;
using Microsoft.Win32;

namespace BandKey.Windows;

internal sealed class VmGuardedSession : IInteractiveSession
{
    private readonly WindowsSessionController session = new();
    public bool IsInteractive => session.IsInteractive;
    public bool? IsLocked() => session.IsLocked();
    public LockRequestResult RequestLock()
    {
        try { VmNativePolicy.RequireGuest(); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or IOException) { return new(false, "guest-required"); }
        return session.RequestLock();
    }
}

public static class VmNativePolicy
{
    private const string Config = @"SOFTWARE\BandKey\CredentialProviderLab";
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BandKeyVmLab");
    public static void RequireGuest()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var bios = machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var version = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if ((string?)bios?.GetValue("SystemManufacturer") != "VMware, Inc." || !((string?)bios?.GetValue("SystemProductName") ?? "").Contains("VMware", StringComparison.Ordinal) ||
            (string?)version?.GetValue("CurrentBuildNumber") != "26300" || SystemInformation.TerminalServerSession || Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("This experiment requires the local VMware Windows 11 build 26300 guest.");
    }
    public static bool MatchesPhone(ReadOnlySpan<byte> nativeKey)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            RequireGuest(); using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); using var key = machine.OpenSubKey(Config);
            return key?.GetValue("Enabled") is int enabled && enabled == 1 && key.GetValue("Mode") is int mode && mode == 2 && key.GetValue("SignerKind") is int kind && kind == 1 &&
                key.GetValue("TargetSid") is string sid && sid == identity.User?.Value && key.GetValue("LabPublicKey") is byte[] pinned && nativeKey.SequenceEqual(pinned);
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
    }
    public static byte[]? ReadEnrolledPhone()
    {
        try{
            RequireGuest();using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var config=machine.OpenSubKey(Config);if(config?.GetValue("LabPublicKey") is not byte[] key || !MatchesPhone(key))return null;
            using var parsed=WindowsGrantWire.ImportNativePublicKey(key);return key.ToArray();
        }catch(Exception error)when(error is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException or FormatException or CryptographicException or ArgumentException){return null;}
    }
    public static bool SupportsRequestRenewal()
    {
        RequireGuest(); using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var config=machine.OpenSubKey(Config);return config?.GetValue("ProtocolVersion") is int version && version==3;
    }
}

/// <summary>Unprivileged public-file transport. It cannot read the vault or Windows credentials.</summary>
public sealed class WindowsGrantDirectoryExchange : IWindowsGrantExchange, IWindowsRequestRenewalExchange
{
    private readonly string root, sid;
    private readonly uint session;
    private readonly Func<bool> current;
    private readonly Func<ulong> now;
    public WindowsRequestBinding Binding=>new(sid,session);
    public WindowsGrantDirectoryExchange(string directory, string sid, uint session, Func<bool> current, Func<ulong>? now = null)
    { root = Path.GetFullPath(directory); this.sid = sid; this.session = session; this.current = current; this.now = now ?? (() => WindowsGrantRelayWire.FileTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())); }
    private void CheckDirectory()
    {
        foreach (var path in new[] { root, Path.Combine(root, "Exchange") })
        {
            var item = new DirectoryInfo(path);
            if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native exchange unavailable");
        }
    }
    private static byte[] ReadBody(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unexpected exchange entry");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read|FileShare.Delete);
        if (stream.Length != WindowsGrantWire.ChallengeSize) throw new IOException("Native request size");
        var body = new byte[WindowsGrantWire.ChallengeSize]; stream.ReadExactly(body); return body;
    }
    private static string Name(WindowsGrantChallenge challenge) => Convert.ToHexStringLower(Convert.FromBase64String(challenge.NonceBase64)) + ".request";
    public byte[]? ReadCurrentChallenge()
    {
        if (!current()) return null; CheckDirectory();
        byte[]? candidate = null; ulong issued = 0; int count = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(Path.Combine(root, "Exchange"), "*.request"))
        {
            if (++count > 64) throw new IOException("Too many exchange entries");
            try
            {
                var body = ReadBody(path); var c = WindowsGrantWire.ReadChallenge(body);
                if (Path.GetFileName(path) != Name(c) || c.UserSid != sid || c.WindowsSessionId != session || !WindowsGrantWire.IsCurrent(c, now())) continue;
                if (candidate == null || c.IssuedFileTimeMilliseconds > issued) { candidate = body; issued = c.IssuedFileTimeMilliseconds; }
            }
            catch (Exception error) when (error is IOException or FormatException or ArgumentException) { /* A malformed public file never becomes an authorization. */ }
        }
        return current() ? candidate : null;
    }
    public bool Submit(ReadOnlySpan<byte> expectedChallenge, ReadOnlySpan<byte> approval)
    {
        if (!current() || approval.Length != WindowsGrantWire.ApprovalSize || !approval[..WindowsGrantWire.ChallengeSize].SequenceEqual(expectedChallenge)) return false;
        var c = WindowsGrantWire.ReadChallenge(expectedChallenge);
        if (c.UserSid != sid || c.WindowsSessionId != session || !WindowsGrantWire.IsCurrent(c, now())) return false;
        CheckDirectory(); var request = Path.Combine(root, "Exchange", Name(c));
        if (!ReadBody(request).AsSpan().SequenceEqual(expectedChallenge)) return false;
        var temporary = request + ".temporary";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { created = true; stream.Write(approval); stream.Flush(true); }
            if (!current() || !WindowsGrantWire.IsCurrent(c, now()) || !ReadBody(request).AsSpan().SequenceEqual(expectedChallenge)) return false;
            File.Move(temporary, request + ".approval", false); created = false; return true;
        }
        finally { if (created) File.Delete(temporary); }
    }
    public byte[] CreateRequestRenewal()
    {
        if(!current())throw new IOException("Request renewal context unavailable");
        ulong issued=now();return WindowsRequestRenewalWire.Write(new(session,issued,checked(issued+10000),sid,Protocol.Nonce()));
    }
    public bool SubmitRequestRenewal(ReadOnlySpan<byte> proof)
    {
        if(!current() || proof.Length!=WindowsRequestRenewalWire.ProofSize)return false;
        var request=WindowsRequestRenewalWire.Read(proof[..WindowsRequestRenewalWire.Size]);
        if(request.UserSid!=sid || request.WindowsSessionId!=session || !WindowsRequestRenewalWire.IsCurrent(request,now()))return false;
        CheckDirectory();string temporary=Path.Combine(root,"Exchange","request-renewal.tmp"),destination=Path.Combine(root,"Exchange","request-renewal.bin");bool created=false;
        byte[]? Existing()
        {
            try{
                if((File.GetAttributes(destination)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("Unexpected renewal entry");
                using var input=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if(input.Length!=WindowsRequestRenewalWire.ProofSize)throw new IOException("Renewal proof size");
                var bytes=new byte[WindowsRequestRenewalWire.ProofSize];input.ReadExactly(bytes);return bytes;
            }catch(FileNotFoundException){return null;}
        }
        bool ExpiredForThisSession(byte[] bytes)
        {
            var old=WindowsRequestRenewalWire.Read(bytes.AsSpan(0,WindowsRequestRenewalWire.Size));
            return old.UserSid==sid && old.WindowsSessionId==session && old.ExpiresFileTimeMilliseconds<=now();
        }
        var existing=Existing();
        if(existing!=null){
            if(existing.AsSpan().SequenceEqual(proof))return current() && WindowsRequestRenewalWire.IsCurrent(request,now());
            if(!ExpiredForThisSession(existing))return false;
        }
        try{
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;stream.Write(proof);stream.Flush(true);}
            if(!current() || !WindowsRequestRenewalWire.IsCurrent(request,now()))return false;
            // Recheck just before the atomic replace. Never overwrite another live
            // proof, malformed entry, or different account/session. Native still
            // independently verifies the new signature and its ten-second expiry.
            var latest=Existing();
            if(latest!=null && (existing==null || !latest.AsSpan().SequenceEqual(existing) || !ExpiredForThisSession(latest)))return false;
            File.Move(temporary,destination,latest!=null);created=false;return true;
        }finally{if(created)File.Delete(temporary);}
    }
}
