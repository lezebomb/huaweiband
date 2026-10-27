using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BandKey.Core;

namespace BandKey.Windows;

internal interface IBlePhoneConnection : IAsyncDisposable
{
    string Stage{get;}
    BleTransportFailure? Failure{get;}
    ushort Mtu{get;}
    int Streams{get;}
    TimeSpan CompletedStreamSpan=>TimeSpan.Zero;
    long SentBytes{get;}
    long ReceivedBytes{get;}
    string DeliveryMode=>"unknown";
    BlePeerBinding? Peer=>null;
    Task RunAsync(IPAddress target,int port,TimeSpan duration,string? readyFile=null,string? stopFile=null,CancellationToken cancellation=default);
}

internal sealed record BleAttemptFailure(int Attempt,long ElapsedMilliseconds,string Stage,BleTransportFailure Failure);

/// <summary>Retries availability faults with new GATT state; never retries an application decision.</summary>
internal sealed class BlePhoneTransport
{
    private readonly Func<IBlePhoneConnection> factory;
    private readonly Func<int,CancellationToken,Task> delay;
    private readonly List<BleAttemptFailure> failures=[];
    private readonly object failureLock=new();
    private IBlePhoneConnection? current;
    private int started,streams;
    private long sent,received;
    private ushort mtu=23;
    private string stage="not-started";
    private string deliveryMode="none";
    public string DeliveryMode=>current?.DeliveryMode??deliveryMode;
    public BlePeerBinding? Peer=>current?.Peer;
    public string Stage=>current?.Stage??stage;
    public ushort Mtu=>current?.Mtu??mtu;
    public int Streams=>streams+(current?.Streams??0);
    public int CurrentAttemptStreams=>current?.Streams??0;
    public long SentBytes=>sent+(current?.SentBytes??0);
    public long ReceivedBytes=>received+(current?.ReceivedBytes??0);
    public int Attempts{get;private set;}
    public int FailureCount{get;private set;}
    public int ConsecutiveFailures{get;private set;}
    public int RecoveryBudgetResets{get;private set;}
    public bool FailuresTruncated{get{lock(failureLock)return FailureCount>failures.Count;}}
    public BleAttemptFailure[] Failures{get{lock(failureLock)return failures.ToArray();}}
    public BleTransportFailure? Failure{get{lock(failureLock)return failures.LastOrDefault()?.Failure;}}
    // The v1 phone can still be awaiting READY for up to eight seconds after
    // CCCD removal. Let that old worker and OS GATT references drain first.
    // The index is the current failure burst, not the lifetime attempt count.
    public BlePhoneTransport():this(()=>new BlePhoneTunnel(),(burst,token)=>Task.Delay(TimeSpan.FromSeconds(10+(1<<(burst-1))),token)){}
    internal BlePhoneTransport(Func<IBlePhoneConnection> factory,Func<int,CancellationToken,Task> delay){this.factory=factory;this.delay=delay;}

    private static bool Retryable(BleTransportFailure failure)=>failure.Code is
        "gatt-disconnected" or "gatt-write-unreachable" or "gatt-write-timeout" or
        "gatt-idle-subscription-lost" or "gatt-health-timeout" or "gatt-health-unreachable" or "gatt-health-protocolerror" or "gatt-health-failed" or
        "target-connect" or "target-connect-timeout" or "target-read" or "target-write" or "target-half-close" or "stream-timeout" or
        "connect-scan" or "connect-device" or "connect-service" or "connect-subscribe" or "connect-health-descriptor";

    public async Task RunAsync(IPAddress target,int port,TimeSpan duration,string? readyFile=null,string? stopFile=null,CancellationToken cancellation=default)
    {
        if(!LoopbackTcpTunnel.IsLocalTarget(target) || port is <1 or >65535 || duration<=TimeSpan.Zero || duration>TimeSpan.FromHours(2))throw new ArgumentException("BLE target/duration");
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Create a new BLE transport before starting again");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);lifetime.CancelAfter(duration);var token=lifetime.Token;
        var elapsed=Stopwatch.StartNew();
        async Task WatchStop()
        {
            while(!token.IsCancellationRequested)
            {
                if(stopFile!=null && File.Exists(stopFile)){lifetime.Cancel();return;}
                try{await Task.Delay(100,token);}catch(OperationCanceledException){return;}
            }
        }
        var watching=WatchStop();
        try
        {
            while(!token.IsCancellationRequested)
            {
                var remaining=duration-elapsed.Elapsed;
                if(remaining<=TimeSpan.Zero){stage="stopped";break;}
                bool retry=false;Attempts++;current=factory();
                try
                {
                    await current.RunAsync(target,port,remaining,readyFile,null,token);
                    stage="stopped";return;
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){stage="stopped";return;}
                catch(Exception error)when(error is IOException or SocketException or TimeoutException or OperationCanceledException)
                {
                    var failure=current.Failure??BleTransportFailure.From(error,"connect-"+current.Stage);
                    lock(failureLock){
                        FailureCount++;failures.Add(new(Attempts,elapsed.ElapsedMilliseconds,current.Stage,failure));
                        if(failures.Count>32)failures.RemoveAt(0);
                    }
                    // Mere connection, idle time or partial bytes do not replenish
                    // retries. Require multiple completed streams spanning a minute
                    // in this generation, while preserving the original run deadline.
                    if(ConsecutiveFailures>0 && current.Streams>=3 && current.CompletedStreamSpan>=TimeSpan.FromSeconds(60)){
                        ConsecutiveFailures=0;RecoveryBudgetResets++;
                    }
                    ConsecutiveFailures++;
                    stage="failed";retry=ConsecutiveFailures<4 && Retryable(failure);
                    if(!retry)throw;
                }
                finally
                {
                    // Dispose every old subscription/channel/socket before another attempt.
                    var ended=current;await ended.DisposeAsync();
                    streams+=ended.Streams;sent+=ended.SentBytes;received+=ended.ReceivedBytes;mtu=ended.Mtu;deliveryMode=ended.DeliveryMode;current=null;
                    if(readyFile!=null)File.Delete(readyFile);
                }
                if(retry){stage="reconnecting";await delay(ConsecutiveFailures,token);}
            }
            stage="stopped";
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){stage="stopped";}
        finally{lifetime.Cancel();await watching;}
    }
}
