using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using BandKey.Core;
using Microsoft.Win32;

namespace BandKey.Windows;

// Explicit host-pilot scope. The laboratory's static guard is never relaxed.
internal sealed class NativePhonePolicy(bool hostPilot)
{
    internal const string HostConfig=@"SOFTWARE\BandKey\HostPilot";
    internal const string HostProviderGuid="{1DF46BC8-73B9-48B4-8E5A-55EC21143C4E}";
    internal string DirectoryPath=>hostPilot?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"BandKeyHostPilot"):VmNativePolicy.DirectoryPath;
    internal static string MachineFingerprint(string machine)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(machine.ToLowerInvariant())));
    internal void RequireEnvironment()
    {
        if(!hostPilot){VmNativePolicy.RequireGuest();return;}
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var bios=machine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var version=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string? maker=bios?.GetValue("SystemManufacturer") as string;
        if(string.IsNullOrEmpty(maker) || maker=="VMware, Inc." || !Environment.Is64BitProcess || !Environment.UserInteractive ||
            Process.GetCurrentProcess().SessionId==0 || SystemInformation.TerminalServerSession ||
            !int.TryParse(version?.GetValue("CurrentBuildNumber") as string,out int build) || build<22000)
            throw new InvalidOperationException("A local physical Windows 11 x64 session is required");
    }
    internal bool MatchesPhone(ReadOnlySpan<byte> phone)
    {
        if(!hostPilot)return VmNativePolicy.MatchesPhone(phone);
        try{
            RequireEnvironment();using var identity=WindowsIdentity.GetCurrent();
            using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var key=machine.OpenSubKey(HostConfig);
            using var crypto=machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            using var version=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            using var provider=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\"+HostProviderGuid);
            string? id=crypto?.GetValue("MachineGuid") as string;
            return provider!=null && key?.GetValue("Enabled") is int enabled && enabled==1 && key.GetValue("Mode") is int mode && mode==2 &&
                key.GetValue("SignerKind") is int signer && signer==1 && key.GetValue("AccountKind") is int account && account==1 &&
                key.GetValue("ProtocolVersion") is int protocol && protocol==3 && key.GetValue("DeploymentProfile") as string=="host-pilot-v1" &&
                id!=null && key.GetValue("HostBinding") as string==MachineFingerprint(id) &&
                key.GetValue("TargetBuild") as string==version?.GetValue("CurrentBuildNumber") as string &&
                key.GetValue("TargetSid") as string==identity.User?.Value && key.GetValue("LabPublicKey") is byte[] pinned && phone.SequenceEqual(pinned);
        }catch(Exception error)when(error is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException){return false;}
    }
    internal byte[]? ReadEnrolledPhone()
    {
        if(!hostPilot)return VmNativePolicy.ReadEnrolledPhone();
        try{RequireEnvironment();using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);using var key=machine.OpenSubKey(HostConfig);
            if(key?.GetValue("LabPublicKey") is not byte[] phone || !MatchesPhone(phone))return null;
            using var parsed=WindowsGrantWire.ImportNativePublicKey(phone);return phone;
        }catch(Exception error)when(error is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException or FormatException or CryptographicException or ArgumentException){return null;}
    }
    internal bool SupportsRequestRenewal()=>hostPilot?ReadEnrolledPhone()!=null:VmNativePolicy.SupportsRequestRenewal();
}

internal sealed class NativeGuardedSession(NativePhonePolicy policy):IInteractiveSession
{
    private readonly WindowsSessionController session=new();
    public bool IsInteractive=>session.IsInteractive;
    public bool? IsLocked()=>session.IsLocked();
    public LockRequestResult RequestLock(){try{policy.RequireEnvironment();}catch(Exception error)when(error is InvalidOperationException or UnauthorizedAccessException or IOException){return new(false,"environment-required");}return session.RequestLock();}
}
