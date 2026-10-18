using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BandKey.Windows;

/// <summary>Opaque local transport: TLS terminates at the VM, never at this forwarder.</summary>
public sealed class LoopbackTcpTunnel : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly IPEndPoint target;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentDictionary<int,Task> connections=new();
    private readonly SemaphoreSlim slots;
    private readonly TimeSpan connectionLifetime;
    private Task? accepting;
    private int sequence,accepted,rejected,failed;
    public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
    public int Accepted=>Volatile.Read(ref accepted);
    public int Rejected=>Volatile.Read(ref rejected);
    public int Failed=>Volatile.Read(ref failed);
    public LoopbackTcpTunnel(int listenPort,IPAddress targetAddress,int targetPort,int maxConnections=4,TimeSpan? connectionLifetime=null)
    {
        if(listenPort is <0 or >65535 || targetPort is <1 or >65535 || maxConnections is <1 or >16 || !IsLocalTarget(targetAddress))throw new ArgumentException("Tunnel requires loopback or private IPv4 target and bounded ports/connections");
        this.connectionLifetime=connectionLifetime??TimeSpan.FromMinutes(2);
        if(this.connectionLifetime<=TimeSpan.Zero || this.connectionLifetime>TimeSpan.FromMinutes(10))throw new ArgumentException("Connection lifetime");
        listener=new(IPAddress.Loopback,listenPort);target=new(targetAddress,targetPort);slots=new(maxConnections,maxConnections);
    }
    public static bool IsLocalTarget(IPAddress address)
    {
        if(IPAddress.IsLoopback(address))return true;
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
        var b=address.GetAddressBytes();return b[0]==10 || (b[0]==172 && b[1] is >=16 and <=31) || (b[0]==192 && b[1]==168);
    }
    public void Start()
    {
        if(accepting!=null)throw new InvalidOperationException("Tunnel already started");
        listener.Start(8);accepting=AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                if(!slots.Wait(0)){Interlocked.Increment(ref rejected);client.Dispose();continue;}
                Interlocked.Increment(ref accepted);int id=Interlocked.Increment(ref sequence);
                var task=ForwardAsync(client);connections[id]=task;
                _=task.ContinueWith(_=>{connections.TryRemove(id,out var completed);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(SocketException)when(stop.IsCancellationRequested){}
    }
    private async Task ForwardAsync(TcpClient client)
    {
        using(client)
        using(var remote=new TcpClient(target.AddressFamily))
        using(var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            lifetime.CancelAfter(connectionLifetime);
            try
            {
                using(var connecting=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {connecting.CancelAfter(TimeSpan.FromSeconds(3));await remote.ConnectAsync(target.Address,target.Port,connecting.Token);}
                client.NoDelay=remote.NoDelay=true;
                var clientStream=client.GetStream();var remoteStream=remote.GetStream();
                async Task Pump(NetworkStream input,NetworkStream output,Socket destination)
                {
                    try{await input.CopyToAsync(output,65536,lifetime.Token);destination.Shutdown(SocketShutdown.Send);}
                    catch {lifetime.Cancel();throw;}
                }
                await Task.WhenAll(Pump(clientStream,remoteStream,remote.Client),Pump(remoteStream,clientStream,client.Client));
            }
            catch(Exception error)when(error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {if(!stop.IsCancellationRequested)Interlocked.Increment(ref failed);}
            finally{slots.Release();}
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();if(accepting!=null)await accepting;
        await Task.WhenAll(connections.Values);slots.Dispose();stop.Dispose();
    }
}
