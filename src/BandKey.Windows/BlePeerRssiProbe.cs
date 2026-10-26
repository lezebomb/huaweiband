using BandKey.Core;

namespace BandKey.Windows;

// Optional radio diagnostics. No identity promotion, locking, approval or pairing.
internal sealed class BlePeerRssiProbe : IDisposable
{
    private readonly object gate=new();
    private readonly Func<BlePeerBinding?> peer;
    private readonly BleScanner scanner=new();
    private readonly HashSet<string> generations=new(StringComparer.Ordinal);
    private long samples;
    private int? minimum,maximum,last;
    private DateTimeOffset? seenAt;
    private double maxGap;
    private string? error;
    private bool disposed;
    public BlePeerRssiProbe(Func<BlePeerBinding?> peer)
    {this.peer=peer;scanner.Observed+=OnObserved;scanner.Failed+=OnFailed;}
    public void Start(){try{scanner.Start();}catch(Exception exception){OnFailed(exception.GetType().Name);}}
    private void OnFailed(string code){lock(gate){error=code;}}
    private void OnObserved(DeviceObservation sample)
    {
        lock(gate){
            if(disposed)return;var current=peer();
            if(current==null || !current.Target.Matches(sample) || sample.SeenAt<current.StartedAt ||
                sample.SeenAt>DateTimeOffset.UtcNow || (DateTimeOffset.UtcNow-sample.SeenAt).TotalSeconds>=5 || sample.Rssi is < -120 or > -10)return;
            if(seenAt.HasValue && sample.SeenAt<=seenAt.Value)return;
            if(seenAt.HasValue)maxGap=Math.Max(maxGap,(sample.SeenAt-seenAt.Value).TotalMilliseconds);
            generations.Add(current.ConnectionId);samples++;last=sample.Rssi;seenAt=sample.SeenAt;
            minimum=minimum.HasValue?Math.Min(minimum.Value,sample.Rssi):sample.Rssi;
            maximum=maximum.HasValue?Math.Max(maximum.Value,sample.Rssi):sample.Rssi;
        }
    }
    public object Snapshot{get{lock(gate){return new{
        scannerHealthy=scanner.Healthy,error,samples,sampledConnectionGenerations=generations.Count,
        minimumRssi=minimum,maximumRssi=maximum,lastRssi=last,maxSampleGapMilliseconds=maxGap,
        lastSampleAgeMilliseconds=seenAt.HasValue?(double?)Math.Max(0,(DateTimeOffset.UtcNow-seenAt.Value).TotalMilliseconds):null,
        peerIdentityAuthenticatedByProbe=false,proximityControlEnabled=false,windowsAuthenticationSubmitted=false
    };}}}
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;}scanner.Observed-=OnObserved;scanner.Failed-=OnFailed;scanner.Dispose();}
}
