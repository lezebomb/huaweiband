using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Principal;
using BandKey.Core;
using Microsoft.Win32;
using Windows.Security.Credentials.UI;

namespace BandKey.Windows;

public sealed record HostReadinessReport(int WindowsBuild,string AccountKind,uint? AccountQueryStatus,bool? IdentitySidMatched,
    bool InteractiveSession,bool RemoteSession,string HelloAvailability,int? HelloQueryHResult,bool VmLabProviderRegistered,bool AuthenticationBackendAvailable,string[] RemainingWork)
{
    public string Schema=>"bandkey.host-readiness.v1";
    public bool InstallReady=>false; // This command cannot authorize or install a provider.
    public bool MutationPerformed=>false;
    public bool WindowsAuthenticationSubmitted=>false;
    public bool VerificationPromptRequested=>false;
}

/// <summary>Local read-only capability inspection. No credential, registry-write or authentication APIs.</summary>
public static class HostReadinessAudit
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo24
    {
        [MarshalAs(UnmanagedType.Bool)]public bool InternetIdentity;
        public uint Flags;
        public IntPtr ProviderName,PrincipalName,UserSid;
    }
    [DllImport("Netapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern uint NetUserGetInfo(string? server,string user,uint level,out IntPtr buffer);
    [DllImport("Netapi32.dll",ExactSpelling=true)]private static extern uint NetApiBufferFree(IntPtr buffer);
    private static (string Kind,uint? Status,bool? SidMatched) Account()
    {
        IntPtr buffer=IntPtr.Zero;
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            uint status=NetUserGetInfo(null,Environment.UserName,24,out buffer);
            if(status!=0 || buffer==IntPtr.Zero)return ("Unknown",status,null);
            var info=Marshal.PtrToStructure<UserInfo24>(buffer);
            if(info.Flags!=0)return ("Unknown",status,null);
            if(info.InternetIdentity)
            {
                // Do not read or export the principal name, account name or SID.
                bool matches=info.UserSid!=IntPtr.Zero && identity.User==new SecurityIdentifier(info.UserSid);
                if(!matches)return ("Unknown",status,false);
                string provider=Marshal.PtrToStringUni(info.ProviderName)??"";
                return (provider.Equals("MicrosoftAccount",StringComparison.OrdinalIgnoreCase)?"MicrosoftAccount":"OtherInternetIdentity",status,true);
            }
            string qualified=identity.Name;int separator=qualified.IndexOf('\\');
            bool local=separator>0 && qualified[..separator].Equals(Environment.MachineName,StringComparison.OrdinalIgnoreCase);
            return (local?"LocalAccount":"DomainOrCloudAccount",status,null);
        }
        catch(Exception error)when(error is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception){return ("Unknown",null,null);}
        finally{if(buffer!=IntPtr.Zero)NetApiBufferFree(buffer);}
    }
    public static async Task<HostReadinessReport> CaptureAsync()
    {
        var account=Account();string hello="Unknown";int? helloError=null;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            hello=(await UserConsentVerifier.CheckAvailabilityAsync().AsTask(timeout.Token).ConfigureAwait(false)).ToString();
        }
        catch(OperationCanceledException){hello="TimedOut";}
        catch(Exception error){helloError=error.HResult;}
        int build=Environment.OSVersion.Version.Build;bool registered=false;
        using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
        {
            using var version=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if(int.TryParse(version?.GetValue("CurrentBuildNumber") as string,out int actual))build=actual;
            using var lab=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{76B424B6-A72D-4E98-9DC1-485E37A5429C}");
            registered=lab!=null;
        }
        bool backend=new UnavailableAuthenticationBackend().IsAvailable;
        var work=new List<string>{"ble-indication-and-background-hardware-validation","physical-host-provider-and-rollback-validation"};
        if(!backend)work.Add(account.Kind=="MicrosoftAccount"?"microsoft-account-authentication-backend-unimplemented":"physical-host-authentication-backend-unimplemented");
        if(account.Kind=="Unknown")work.Add("account-type-query-incomplete");
        if(build<22000)work.Add("windows-11-required");
        bool interactive=Environment.UserInteractive && Process.GetCurrentProcess().SessionId!=0;
        bool remote=SystemInformation.TerminalServerSession;
        if(!interactive || remote)work.Add("local-interactive-session-required");
        return new(build,account.Kind,account.Status,account.SidMatched,interactive,remote,hello,helloError,registered,backend,work.ToArray());
    }
}
