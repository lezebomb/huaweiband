using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
        try
        {
            using var connecting=CancellationTokenSource.CreateLinkedTokenSource(token);
            connecting.CancelAfter(TimeSpan.FromSeconds(3));await remote.ConnectAsync(target,port,connecting.Token);
        }
        catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new BleTransportException("target-connect-timeout");}
        catch(SocketException error){throw new BleTransportException("target-connect",error);}
        remote.NoDelay=true;var network=remote.GetStream();
        try{await write(new(BlePipeFrame.Ready,open.Session,sequence++,[]),token);}
        catch(OperationCanceledException error)when(!cancellation.IsCancellationRequested){throw new BleTransportException("stream-timeout",error);}
        catch(Exception error)when(error is not OperationCanceledException){throw new BleTransportException("ready-write",error);}
        Exception? firstFailure=null;int phoneEnded=0,targetEnded=0;
        Exception Capture(Exception error,string phase)
        {
            Exception failure=error;
            if(error is OperationCanceledException && !cancellation.IsCancellationRequested)
                failure=new BleTransportException("stream-timeout",error);
            else if(error is not (OperationCanceledException or BleTransportException))
                failure=new BleTransportException(phase,error);
            if(failure is BleTransportException coded)
                failure=new BleTransportException(coded.Code,coded,new(Volatile.Read(ref phoneEnded)!=0,Volatile.Read(ref targetEnded)!=0,Interlocked.Read(ref sent),Interlocked.Read(ref received)));
            // Preserve the actual first fault before cancelling the other direction.
            Interlocked.CompareExchange(ref firstFailure,failure,null);lifetime.Cancel();return failure;
        }
        async Task Upload()
        {
            string phase="phone-read";
            try
            {
                while(true)
                {
                    phase="phone-read";var frame=await read(token);
                    phase="phone-frame-invalid";receive.Accept(frame);
                    if(frame.Kind==BlePipeFrame.Abort)throw new BleTransportException("phone-aborted");
                    if(frame.Kind==BlePipeFrame.Eof)
                    {
                        Interlocked.Exchange(ref phoneEnded,1);phase="target-half-close";
                        // Once both EOFs have arrived, disposing the completed socket is
                        // sufficient. Shutdown on a peer that already closed can reset it.
                        if(Volatile.Read(ref targetEnded)==0)remote.Client.Shutdown(SocketShutdown.Send);
                        return;
                    }
                    phase="target-write";await network.WriteAsync(frame.Payload,token);received+=frame.Payload.Length;
                }
            }
            catch(Exception error){throw Capture(error,phase);}
        }
        async Task Download()
        {
            string phase="target-read";
            try
            {
                while(true)
                {
                    var size=payloadLimit();if(size is <1 or >BlePipeFrame.MaxPayload)throw new BleTransportException("payload-limit");
                    byte[] bytes=new byte[size];phase="target-read";int count=await network.ReadAsync(bytes,token);
                    phase="phone-write";
                    if(count==0){Interlocked.Exchange(ref targetEnded,1);await write(new(BlePipeFrame.Eof,open.Session,sequence++,[]),token);return;}
                    sent+=count;if(sent>BlePipeFrame.MaxBytes)throw new BleTransportException("stream-byte-limit");
                    await write(new(BlePipeFrame.Data,open.Session,sequence++,bytes[..count]),token);
                }
            }
            catch(Exception error){throw Capture(error,phase);}
        }
        try{await Task.WhenAll(Upload(),Download());}
        catch{if(firstFailure!=null)ExceptionDispatchInfo.Capture(firstFailure).Throw();throw;}
        return new(sent,received);
    }
}
