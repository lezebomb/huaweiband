using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BandKey.Core;
using BandKey.Windows;

// Ordinary-process host fixture. It has no Provider, Vault, lock or account-authentication calls.
internal static class PhoneUsbLab
{
    private const string Sid="S-1-5-21-111-222-333-1001";
    private sealed class RecordingExchange(WindowsGrantDirectoryExchange files,string root) : IWindowsGrantExchange
    {
        internal int Submissions;
        public byte[]? ReadCurrentChallenge()=>files.ReadCurrentChallenge();
        public bool Submit(ReadOnlySpan<byte> expected,ReadOnlySpan<byte> approval)
        {
            if(!files.Submit(expected,approval))return false;
            File.WriteAllBytes(Path.Combine(root,"native-challenge.bin"),expected.ToArray());
            File.WriteAllBytes(Path.Combine(root,"phone-approval.bin"),approval.ToArray());Submissions++;return true;
        }
    }
    internal static async Task<int> RunAsync(string[] args)
    {
        string Arg(string name){int index=Array.IndexOf(args,name);return index>=0 && index+1<args.Length?args[index+1]:throw new ArgumentException(name);}
        string root=Path.GetFullPath(Arg("--root"));Directory.CreateDirectory(root);Directory.CreateDirectory(Path.Combine(root,"Exchange"));
        if(args.Contains("--new-request")){NewRequest(root);return 0;}
        using var phone=WindowsGrantWire.ImportNativePublicKey(File.ReadAllBytes(Path.Combine(root,"phone-public.bin")));
        var pub=Protocol.ExportPublicKey(phone);var device=new PublicDevice(Protocol.Fingerprint(pub),"Physical phone USB test",pub,ConfirmationSources.TestClient,true);
        var store=new Store(Path.Combine(root,"host-identity"));using var pc=store.LoadSigningKey();using var cert=store.LoadTlsCertificate();
        bool Current()=>!File.Exists(Path.Combine(root,"revoked"));
        var exchange=new RecordingExchange(new(root,Sid,5,Current),root);var relay=new WindowsGrantRelay(pc,device,exchange,Current);
        await using var server=new BridgeServer();await server.StartAsync(new AppSettings{ListenAddress=IPAddress.Loopback.ToString(),Port=19445},cert,new AuthorizationBroker(relay.PcId,pc),_=>{},windowsRelay:relay);
        bool ble=args.Contains("--ble");await using var tunnel=ble?null:new LoopbackTcpTunnel(19444,IPAddress.Loopback,19445);tunnel?.Start();
        File.WriteAllText(Path.Combine(root,"pc.json"),JsonSerializer.Serialize(new{endpoint="https://127.0.0.1:19444",pcId=relay.PcId,pcPublicKey=Protocol.ExportPublicKey(pc),certificateSha256=BridgeServer.CertificatePin(cert),protocol="bandkey-vm-phone-v1",testOnly=true,purpose=ble?"bandkey-ble-instrumentation-v1":"bandkey-usb-instrumentation-v1",singleConnectionTransport=ble},Store.Json));
        if(!args.Contains("--no-request"))NewRequest(root);
        File.WriteAllText(Path.Combine(root,"ready"),"ready");
        try {for(int i=0;i<600 && !File.Exists(Path.Combine(root,"stop"));i++)await Task.Delay(1000);}
        finally {relay.Revoke();File.WriteAllText(Path.Combine(root,"host-result.json"),JsonSerializer.Serialize(new{submissions=exchange.Submissions,tunnelAccepted=tunnel?.Accepted??0,bleFixture=ble,loopbackOnly=true,tlsTerminated=false,syntheticAccount=true,windowsAuthenticationSubmitted=false,realWearableTested=false},Store.Json));}
        return 0;
    }
    private static void NewRequest(string root)
    {
        ulong now=WindowsGrantRelayWire.FileTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var body=WindowsGrantWire.WriteChallenge(new(5,now,now+30000,Sid,Protocol.Nonce()));
        File.WriteAllBytes(Path.Combine(root,"Exchange",Convert.ToHexStringLower(body.AsSpan(36,32))+".request"),body);
    }
}
