namespace BandKey.Core;

// A local GATT connection generation, not an authenticated device identity.
public sealed record BlePeerBinding(string ConnectionId, ObservationTarget Target, DateTimeOffset StartedAt);

// Supplied only by the authentication layer after verifying a signed message on
// this exact local BLE connection. Never deserialize this as trusted HTTP input.
public sealed record VerifiedPhoneLink(string ConnectionId,string PhoneId,long VerifiedAtMonotonicMilliseconds,bool? BandConnected);
public sealed record CarriedPhoneSnapshot(PresenceSnapshot Presence,bool LinkBound,bool Authenticated,bool BandConnected,
    bool CanRequestConfirmation,long AcceptedSamples,long IgnoredSamples,string Reason);

/// <summary>Combines selected-link RSSI and verified phone evidence. Proximity never signs an approval.</summary>
public sealed class CarriedPhonePresence
{
    private readonly object gate=new();
    private readonly string phoneId;
    private readonly Func<BlePeerBinding?> link;
    private readonly Func<VerifiedPhoneLink?> verified;
    private readonly Func<bool> sourceHealthy,contextCurrent;
    private readonly Func<long> mono;
    private readonly Func<DateTimeOffset> wall;
    private readonly IInteractiveSession session;
    private readonly AutoLockController control;
    private PresenceOptions options;
    private BlePeerBinding? bound;
    private string? blockedConnection;
    private long lastMono=long.MinValue,accepted,ignored;
    private DateTimeOffset? lastObservation;
    private bool connected,authenticated,bandConnected,revoked;
    private string reason="startup";
    public CarriedPhonePresence(string phoneId,PresenceOptions options,IInteractiveSession session,
        Func<BlePeerBinding?> link,Func<VerifiedPhoneLink?> verified,Func<bool> sourceHealthy,Func<bool> contextCurrent,
        Func<long>? monotonicMilliseconds=null,Func<DateTimeOffset>? utcNow=null,bool autoLock=false)
    {
        if(phoneId==null || phoneId.Length!=64 || phoneId.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new ArgumentException("Enrolled phone fingerprint required");
        this.phoneId=phoneId;this.options=options;this.session=session;this.link=link;this.verified=verified;
        this.sourceHealthy=sourceHealthy;this.contextCurrent=contextCurrent;
        mono=monotonicMilliseconds??(()=>Environment.TickCount64);wall=utcNow??(()=>DateTimeOffset.UtcNow);
        control=new(options,session){Enabled=autoLock};control.SelectInputMode(ProximityInputMode.CarriedPhoneRssi);
    }
    public void Reconfigure(PresenceOptions value,bool autoLock)
    {lock(gate){value.Validate();options=value;control.Reconfigure(value);control.Enabled=autoLock;lastObservation=null;reason="settings-changed";}}
    // Resume or source failure requires a new GATT generation, even if the OS
    // still exposes the old connection and a cached authentication result.
    public void Invalidate(string code="source-invalidated")
    {lock(gate){try{blockedConnection=link()?.ConnectionId??bound?.ConnectionId;}catch(Exception){blockedConnection=bound?.ConnectionId;}Reset(code);}}
    public void Revoke(){lock(gate){revoked=true;Reset("revoked");}}
    private void Reset(string code)
    {connected=authenticated=bandConnected=false;lastObservation=null;control.Reset(code);reason=code;}
    private bool Refresh()
    {
        long now=mono();
        if(now<0 || now<lastMono){revoked=true;Reset("clock-rollback");return false;}
        lastMono=now;
        if(revoked){Reset("revoked");return false;}
        BlePeerBinding? live;VerifiedPhoneLink? proof;bool healthy,current;
        try{live=link();proof=verified();healthy=sourceHealthy();current=contextCurrent();}
        catch(Exception){blockedConnection=bound?.ConnectionId;Reset("source-unavailable");return false;}
        control.SetSession(session.IsLocked());
        if(!current || !healthy){blockedConnection=live?.ConnectionId??bound?.ConnectionId;Reset(!current?"context-unavailable":"scanner-unavailable");return false;}
        connected=authenticated=bandConnected=false;
        if(live==null){blockedConnection=bound?.ConnectionId??blockedConnection;control.Tick(now/1000d,true);reason="link-unavailable";return false;}
        if(live.ConnectionId==null || live.ConnectionId.Length!=32 || live.ConnectionId.Any(c=>!char.IsAsciiHexDigitLower(c)) ||
            live.Target==null || live.Target.Address==null || live.Target.Address.Length!=12 || live.Target.Address.Any(c=>!char.IsAsciiHexDigit(c)) ||
            live.Target.AddressType is not ("Public" or "Random") || live.StartedAt>wall())
        {Reset("binding-invalid");return false;}
        if(live.ConnectionId==blockedConnection){Reset("new-link-required");return false;}
        if(bound?.ConnectionId!=live.ConnectionId){bound=live;Reset("link-changed");}
        else if(bound!=live){blockedConnection=live.ConnectionId;Reset("binding-changed");return false;}
        connected=true;
        if(proof==null || proof.ConnectionId!=live.ConnectionId || proof.PhoneId!=phoneId ||
            proof.VerifiedAtMonotonicMilliseconds<0 || proof.VerifiedAtMonotonicMilliseconds>now || now-proof.VerifiedAtMonotonicMilliseconds>=10000)
        {Reset("phone-authentication-unavailable");connected=true;return false;}
        authenticated=true;bandConnected=proof.BandConnected==true;
        if(!bandConnected){control.Reset("band-connection-unconfirmed");lastObservation=null;reason="band-connection-unconfirmed";return false;}
        control.Tick(now/1000d,true);reason="bound-phone";return true;
    }
    public bool Observe(DeviceObservation sample)
    {
        lock(gate){
            if(!Refresh()){ignored++;return false;}
            var now=wall();double age=(now-sample.SeenAt).TotalSeconds;
            if(sample.Transport!="windows-ble" || !bound!.Target.Matches(sample) || age<0 || age>=options.StaleSeconds ||
                sample.SeenAt<bound.StartedAt || lastObservation.HasValue && sample.SeenAt<=lastObservation.Value || sample.Rssi is < -120 or > -10)
            {ignored++;return false;}
            lastObservation=sample.SeenAt;accepted++;control.ObserveCarriedPhone(sample.Rssi,lastMono/1000d,true);return true;
        }
    }
    public CarriedPhoneSnapshot Tick(){lock(gate){Refresh();return Snapshot();}}
    private CarriedPhoneSnapshot Snapshot()=>new(control.Presence,connected,authenticated,bandConnected,
        connected && authenticated && bandConnected && control.CanRequestConfirmation,accepted,ignored,reason);
    public bool CanRequestConfirmation{get{lock(gate){Refresh();return Snapshot().CanRequestConfirmation;}}}
}
