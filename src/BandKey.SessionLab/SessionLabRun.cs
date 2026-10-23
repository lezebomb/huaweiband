using BandKey.Core;
using BandKey.Windows;

namespace BandKey.SessionLab;

internal sealed class GuestOnlySession(IInteractiveSession inner,Action requireGuest) : IInteractiveSession
{
    public bool IsInteractive=>inner.IsInteractive;
    public bool? IsLocked()=>inner.IsLocked();
    public LockRequestResult RequestLock()
    {
        try{requireGuest();}catch(Exception error)when(error is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException){return new(false,"guest-required");}
        if(!inner.IsInteractive)return new(false,"non-interactive-session");
        if(inner.IsLocked()!=false)return new(false,"state-unknown-or-locked");
        return inner.RequestLock();
    }
}

internal enum SignalScenario { None, Jitter, Away }

internal sealed class SessionLabRun
{
    private readonly IInteractiveSession session;
    private readonly AutoLockController controller;
    private double started,lastSample=-1,currentTime;
    internal SessionEvidence Evidence{get;}
    internal SignalScenario Scenario{get;private set;}
    internal bool ScenarioEverStarted{get;private set;}
    internal PresenceSnapshot Presence=>controller.Presence;
    internal SessionLabRun(IInteractiveSession session,bool preview)
    {
        this.session=session;Evidence=new(preview);
        controller=new(new PresenceOptions{NearSeconds=2,FarSeconds=5,Alpha=0.6,LockOnMissingSignal=false},session);
        controller.LockAttempted+=result=>{Evidence.Request(result,currentTime);StopScenario();};
    }
    internal bool StartScenario(SignalScenario scenario,double elapsed)
    {
        if(!double.IsFinite(elapsed) || elapsed<0)throw new ArgumentException("Monotonic elapsed time required");
        if(scenario==SignalScenario.None || !Enum.IsDefined(scenario))throw new ArgumentException("Choose a signal scenario");
        if(Evidence.Truncated || Evidence.Attempts.Length>=16 || !session.IsInteractive || session.IsLocked()!=false)return false;
        StopScenario();controller.SetSession(false);started=elapsed;currentTime=elapsed;lastSample=-1;
        Scenario=scenario;ScenarioEverStarted=true;controller.Enabled=true;return true;
    }
    internal void StopScenario(){Scenario=SignalScenario.None;controller.Enabled=false;controller.Reset("lab-stopped");}
    internal void Observe(bool? locked,ObservationSource source,double elapsed)
    {
        currentTime=elapsed;Evidence.Observe(locked,source,elapsed);controller.SetSession(locked);
        if(locked!=false)StopScenario();
    }
    internal void Tick(double elapsed)
    {
        currentTime=elapsed;
        if(Scenario==SignalScenario.None)return;
        double second=Math.Floor((elapsed-started)/1000);
        if(second<lastSample)throw new ArgumentException("Scenario clock moved backwards");
        if(second==lastSample)return;
        if(second>=28){StopScenario();return;}
        lastSample=second;
        int rssi=Scenario==SignalScenario.Away && second>=8?-95:Scenario==SignalScenario.Jitter && (int)second%7==6?-95:-55;
        controller.Observe(rssi,elapsed/1000,true);
    }
}
