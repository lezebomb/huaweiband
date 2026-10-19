using System.Net;
using System.Text.Json;
using BandKey.Windows;

internal static class PhoneBleTunnelLab
{
    internal static async Task<int> Run(string root)
    {
        root=Path.GetFullPath(root);await using var tunnel=new BlePhoneTunnel();bool passed=false;string? error=null;
        try{await tunnel.RunAsync(IPAddress.Loopback,19445,TimeSpan.FromSeconds(150),Path.Combine(root,"ble-pc-ready"),Path.Combine(root,"ble-stop"));passed=true;}
        catch(Exception failure){error=failure.GetType().Name;}
        File.WriteAllText(Path.Combine(root,"ble-host-result.json"),JsonSerializer.Serialize(new{passed,error,tunnel.Stage,tunnel.Failure,tunnel.Mtu,tunnel.Streams,tunnel.SentBytes,tunnel.ReceivedBytes,usbDataMappingCreated=false,windowsAuthenticationSubmitted=false},Store.Json));return passed?0:1;
    }
}
