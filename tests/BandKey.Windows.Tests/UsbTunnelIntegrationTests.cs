using System.Net;
using System.Net.Sockets;
using BandKey.Windows;

internal static class UsbTunnelIntegrationTests
{
    internal static async Task RunAsync(Func<string,Func<Task>,Task> test)
    {
        void Check(bool value){if(!value)throw new Exception("USB tunnel assertion");}
        await test("USB tunnel rejects public, multicast and unspecified targets",()=>
        {
            foreach(string address in new[]{"8.8.8.8","0.0.0.0","224.0.0.1","::"}){bool refused=false;try{_ =new LoopbackTcpTunnel(0,IPAddress.Parse(address),12345);}catch(ArgumentException){refused=true;}Check(refused);}
            Check(LoopbackTcpTunnel.IsLocalTarget(IPAddress.Parse("192.168.71.130")));return Task.CompletedTask;
        });
        await test("USB tunnel forwards large bytes both ways and preserves half-close",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();int port=((IPEndPoint)target.LocalEndpoint).Port;
            try
            {
                await using var tunnel=new LoopbackTcpTunnel(0,IPAddress.Loopback,port);tunnel.Start();
                using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,tunnel.Port);using var remote=await target.AcceptTcpClientAsync();
                var clientStream=client.GetStream();var remoteStream=remote.GetStream();
                byte[] up=System.Security.Cryptography.RandomNumberGenerator.GetBytes(300000),down=System.Security.Cryptography.RandomNumberGenerator.GetBytes(200000);
                async Task ReadExact(NetworkStream stream,byte[] expected){byte[] received=new byte[expected.Length];await stream.ReadExactlyAsync(received);Check(received.SequenceEqual(expected));Check(await stream.ReadAsync(new byte[1])==0);}
                var upload=Task.Run(async()=>{await clientStream.WriteAsync(up);client.Client.Shutdown(SocketShutdown.Send);});
                var download=Task.Run(async()=>{await remoteStream.WriteAsync(down);remote.Client.Shutdown(SocketShutdown.Send);});
                await Task.WhenAll(upload,download,ReadExact(remoteStream,up),ReadExact(clientStream,down)).WaitAsync(TimeSpan.FromSeconds(10));Check(tunnel.Accepted==1);
            }
            finally{target.Stop();}
        });
        await test("USB tunnel limits concurrent clients",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();
            try
            {
                await using var tunnel=new LoopbackTcpTunnel(0,IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,1);tunnel.Start();
                using var first=new TcpClient();await first.ConnectAsync(IPAddress.Loopback,tunnel.Port);using var remote=await target.AcceptTcpClientAsync();
                using var second=new TcpClient();await second.ConnectAsync(IPAddress.Loopback,tunnel.Port);byte[] buffer=new byte[1];
                Check(await second.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3))==0);Check(tunnel.Rejected==1);
            }
            finally{target.Stop();}
        });
        await test("USB tunnel closes stalled connections at bounded lifetime",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();
            try
            {
                await using var tunnel=new LoopbackTcpTunnel(0,IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,connectionLifetime:TimeSpan.FromMilliseconds(250));tunnel.Start();
                using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,tunnel.Port);using var remote=await target.AcceptTcpClientAsync();
                Check(await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3))==0);
            }
            finally{target.Stop();}
        });
    }
}
