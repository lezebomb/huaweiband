using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BandKey.Windows;

// Explicit hardware lab only. TLS remains opaque, and no approval is generated here.
internal static class PhoneBleRecoveryLab
{
    internal static async Task<int> Run(string[] args)
    {
        string Arg(string name){int i=Array.IndexOf(args,name);return i>=0 && i+1<args.Length?args[i+1]:throw new ArgumentException("Missing lab option");}
        bool synthetic=args.Contains("--synthetic-fixture");
        var target=IPAddress.Parse(Arg("--target"));int port=synthetic?19445:19443;
        if(!LoopbackTcpTunnel.IsLocalTarget(target) || (synthetic?!IPAddress.IsLoopback(target):IPAddress.IsLoopback(target)))throw new ArgumentException("Lab target does not match the explicit fixture mode");
        var root=Path.GetFullPath(Arg("--root"));Directory.CreateDirectory(root);
        using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=lifetime.Token;
        var clock=Stopwatch.StartNew();var transport=new BlePhoneTransport();
        LoopbackTcpTunnel? proxy=null;Task? running=null;string? error=null;bool passed=false,injected=false;
        int beforeStreams=0,beforeFailures=0,afterAccepted=0;long beforeBytes=0;var cleanupErrors=new List<string>();
        async Task Wait(Func<bool> condition)
        {
            while(!condition())
            {
                if(running is {IsCompleted:true}){await running;throw new IOException("Transport ended before lab condition");}
                await Task.Delay(100,token);
            }
        }
        try
        {
            proxy=new(19443,target,port);proxy.Start();
            running=transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromMinutes(3),Path.Combine(root,"ready"),Path.Combine(root,"stop"),token);
            await Wait(()=>transport.Streams>=6 && transport.SentBytes>0 && transport.ReceivedBytes>0);
            beforeStreams=transport.Streams;beforeBytes=transport.SentBytes+transport.ReceivedBytes;beforeFailures=transport.Failures.Length;
            if(beforeFailures!=0 || proxy.Failed!=0)throw new IOException("Unhealthy baseline before fault injection");
            await proxy.DisposeAsync();proxy=null;injected=true;
            // Keep the port closed until the production supervisor records a failure.
            await Wait(()=>transport.Failures.Length>beforeFailures);
            proxy=new(19443,target,port);proxy.Start();
            await Wait(()=>transport.Attempts>=2 && transport.Streams>=beforeStreams+8 && proxy.Accepted>=8);
            afterAccepted=proxy.Accepted;
            passed=beforeFailures==0 && transport.SentBytes+transport.ReceivedBytes>beforeBytes && transport.Failures.Length==1;
        }
        catch(Exception failure){error=failure.GetType().Name;}
        finally
        {
            lifetime.Cancel();
            if(running!=null)try{await running;}catch(Exception failure){cleanupErrors.Add("transport-"+failure.GetType().Name);}
            if(proxy!=null)try{await proxy.DisposeAsync();}catch(Exception failure){cleanupErrors.Add("proxy-"+failure.GetType().Name);}
        }
        passed&=cleanupErrors.Count==0;
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new
        {
            passed,error,elapsedMilliseconds=clock.ElapsedMilliseconds,physicalBle=true,controlledTcpInterruption=injected,
            targetKind=synthetic?"synthetic-host-fixture":"isolated-vm",syntheticAccount=synthetic,
            windowsAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BlePhoneTunnel).Assembly.Location))).ToLowerInvariant(),
            beforeStreams,beforeFailures,postRecoveryProxyAccepted=afterAccepted,transport.Attempts,transport.Failures,transport.Mtu,transport.Streams,transport.SentBytes,transport.ReceivedBytes,
            tlsTerminated=false,applicationResponseAfterRecoveryIndependentlyObserved=false,automaticApproval=false,windowsAuthenticationSubmitted=false,realWearableTested=false,cleanupErrors
        },Store.Json));
        return passed?0:1;
    }
}
