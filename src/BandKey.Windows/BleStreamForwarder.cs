using System.Net;
using System.Net.Sockets;
using BandKey.Core;

namespace BandKey.Windows;

public sealed record BleStreamStats(long SentBytes,long ReceivedBytes);

/// <summary>One bounded byte stream; the GATT and simulation transports share this exact implementation.</summary>
public static class BleStreamForwarder
{
    public static async Task<BleStreamStats> RunAsync(BlePipeFrame open,IPAddress target,int port,
        Func<CancellationToken,Task<BlePipeFrame>> read,Func<BlePipeFrame,CancellationToken,Task> write,
        Func<int> payloadLimit,CancellationToken cancellation,TimeSpan? duration=null)
    {
        var limit=duration??TimeSpan.FromSeconds(45);
        if(!LoopbackTcpTunnel.IsLocalTarget(target) || port is <1 or >65535 || limit<=TimeSpan.Zero || limit>TimeSpan.FromSeconds(45))throw new ArgumentException("BLE stream target/lifetime");
        var receive=new BlePipeReceiver(open.Session,BlePipeFrame.Open);receive.Accept(open);
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);lifetime.CancelAfter(limit);var token=lifetime.Token;
        using var remote=new TcpClient(target.AddressFamily);uint sequence=0;long sent=0,received=0;
        using(var connecting=CancellationTokenSource.CreateLinkedTokenSource(token)){connecting.CancelAfter(TimeSpan.FromSeconds(3));await remote.ConnectAsync(target,port,connecting.Token);}
        remote.NoDelay=true;var network=remote.GetStream();await write(new(BlePipeFrame.Ready,open.Session,sequence++,[]),token);
        async Task Upload()
        {
            try{while(true){var frame=await read(token);receive.Accept(frame);if(frame.Kind==BlePipeFrame.Abort)throw new IOException("Phone aborted stream");if(frame.Kind==BlePipeFrame.Eof){remote.Client.Shutdown(SocketShutdown.Send);return;}await network.WriteAsync(frame.Payload,token);received+=frame.Payload.Length;}}
            catch{lifetime.Cancel();throw;}
        }
        async Task Download()
        {
            try{while(true){var size=payloadLimit();if(size is <1 or >BlePipeFrame.MaxPayload)throw new IOException("BLE payload limit");byte[] bytes=new byte[size];int count=await network.ReadAsync(bytes,token);if(count==0){await write(new(BlePipeFrame.Eof,open.Session,sequence++,[]),token);return;}sent+=count;if(sent>BlePipeFrame.MaxBytes)throw new IOException("BLE stream byte limit");await write(new(BlePipeFrame.Data,open.Session,sequence++,bytes[..count]),token);}}
            catch{lifetime.Cancel();throw;}
        }
        await Task.WhenAll(Upload(),Download());return new(sent,received);
    }
}
