using BandKey.Core;

namespace BandKey.Windows;

/// <summary>Production passive observations for a live tunnel peer. Disabled until a caller supplies verified session evidence.</summary>
public sealed class CarriedPhoneObservationSource : IDisposable
{
    private readonly IDeviceObservationSource scanner;
    public CarriedPhonePresence Presence{get;}
    private bool disposed;
    public CarriedPhoneObservationSource(string phoneId,PresenceOptions options,IInteractiveSession session,
        Func<BlePeerBinding?> livePeer,Func<VerifiedPhoneLink?> authenticatedPeer,Func<bool> contextCurrent,
        IDeviceObservationSource? scanner=null,bool autoLock=false)
    {
        this.scanner=scanner??new BleScanner();
        Presence=new(phoneId,options,session,livePeer,authenticatedPeer,()=>!disposed && this.scanner.Healthy,contextCurrent,autoLock:autoLock);
        this.scanner.Observed+=OnObserved;this.scanner.Failed+=OnFailed;
    }
    public void Start(){ObjectDisposedException.ThrowIf(disposed,this);scanner.Start();}
    public CarriedPhoneSnapshot Tick()=>Presence.Tick();
    private void OnObserved(DeviceObservation observation)=>Presence.Observe(observation);
    private void OnFailed(string code)=>Presence.Invalidate("scanner-stopped");
    public void ResetAfterResume()=>Presence.Invalidate("power-transition");
    public void Dispose()
    {
        if(disposed)return;disposed=true;Presence.Revoke();
        scanner.Observed-=OnObserved;scanner.Failed-=OnFailed;scanner.Dispose();
    }
}
