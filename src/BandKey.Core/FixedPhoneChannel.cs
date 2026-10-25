using System.Globalization;
using System.Security.Cryptography;

namespace BandKey.Core;

public sealed record FixedPhoneContext(string PcId,string DeviceId,string ContextNonce,long ExpiresAt,long NextSequence,string Signature);
public sealed record BandConnectionReport(string PcId,string DeviceId,string ContextNonce,long Sequence,long ObservedAtMilliseconds,string BandHash,bool? Connected,string Signature);
public sealed record RequestRenewalPrompt(string PcId,string DeviceId,string ContextNonce,string Body,string Signature);
public sealed record RequestRenewalDecision(string PcId,string DeviceId,string ContextNonce,string Body,string Signature);
public sealed record FixedPhoneResult(bool Accepted,bool CanPoll,string Code,RequestRenewalPrompt? Renewal=null,string? NativeState=null)
{ public bool WindowsUnlocked=>false; public bool TestOnly=>true; }
public sealed record FixedPhoneSnapshot(bool? BandConnected,bool Fresh,PresenceState Presence,long Reports,long Renewals,bool NativeRequestReady,bool Revoked,string NativeState="not-requested");

public interface IWindowsRequestRenewalExchange
{
    byte[] CreateRequestRenewal();
    bool SubmitRequestRenewal(ReadOnlySpan<byte> proof);
    byte[]? ReadCurrentChallenge();
}
public static class FixedPhoneWire
{
    public static string ContextPollText(PollRequest p)=>WindowsGrantRelayWire.ProtocolText("bandkey.fixed-phone.context-poll.v1",p.PcId,p.DeviceId,p.Nonce,p.Timestamp.ToString(CultureInfo.InvariantCulture));
    public static string ContextText(FixedPhoneContext c)=>WindowsGrantRelayWire.ProtocolText("bandkey.fixed-phone.context.v1",c.PcId,c.DeviceId,c.ContextNonce,c.ExpiresAt.ToString(CultureInfo.InvariantCulture),c.NextSequence.ToString(CultureInfo.InvariantCulture));
    public static string ReportText(BandConnectionReport p)=>WindowsGrantRelayWire.ProtocolText("bandkey.fixed-phone.state.v1",p.PcId,p.DeviceId,p.ContextNonce,p.Sequence.ToString(CultureInfo.InvariantCulture),p.ObservedAtMilliseconds.ToString(CultureInfo.InvariantCulture),p.BandHash,p.Connected switch{true=>"true",false=>"false",_=>"unknown"});
    public static string RenewalText(RequestRenewalPrompt p)=>WindowsGrantRelayWire.ProtocolText("bandkey.fixed-phone.renewal.v1",p.PcId,p.DeviceId,p.ContextNonce,p.Body);
}

/// <summary>VM-only phone-at-computer experiment. Presence may create requests; it never approves a login.</summary>
public sealed class FixedPhoneChannel
{
    private readonly object gate=new();
    private readonly ECDsa pc;
    private readonly PublicDevice phone;
    private readonly byte[] publicKey;
    private readonly IWindowsRequestRenewalExchange exchange;
    private readonly IInteractiveSession session;
    private readonly Func<bool> current;
    private readonly Func<long> wall,mono;
    private readonly AutoLockController control;
    private readonly Dictionary<string,long> polls=new(StringComparer.Ordinal);
    private readonly string contextNonce=Protocol.Nonce();
    private readonly long startedMono,expiresAt;
    private long latestWall=long.MinValue,latestMono=long.MinValue,sequence,reports,renewals;
    private long freshUntil;
    private bool revoked,arrivalHandled,submitted;
    private bool? bandConnected,lastLocked;
    private string? bandHash; private string nativeState="not-requested";
    private byte[]? renewalBody,previousNative,readyNative;
    public string PcId{get;}
    public FixedPhoneChannel(ECDsa pc,PublicDevice phone,IWindowsRequestRenewalExchange exchange,IInteractiveSession session,Func<bool> current,
        bool autoLock=false,Func<long>? wall=null,Func<long>? mono=null,PresenceOptions? options=null)
    {
        if(!phone.TestOnly || phone.ConfirmationSource!=ConfirmationSources.TestClient || phone.DeviceId!=Protocol.Fingerprint(phone.PublicKey))throw new ArgumentException("Explicit VM phone enrollment required");
        using var key=Protocol.ReadPublicKey(phone.PublicKey);publicKey=WindowsGrantWire.ExportNativePublicKey(key);
        this.pc=pc;this.phone=phone;this.exchange=exchange;this.session=session;this.current=current;
        this.wall=wall??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());this.mono=mono??(()=>Environment.TickCount64);
        startedMono=this.mono();expiresAt=checked(this.wall()/1000+1800);PcId=Protocol.Fingerprint(Protocol.ExportPublicKey(pc));
        control=new(options??new(),session){Enabled=autoLock};control.SelectInputMode(ProximityInputMode.FixedPhoneBandConnection);
    }
    public void Revoke(){lock(gate){revoked=true;ResetArrival();control.Reset("fixed-phone-revoked");}}
    public void RetryRequest(){lock(gate){ResetArrival();}}
    private void ResetArrival(){arrivalHandled=submitted=false;renewalBody=previousNative=readyNative=null;}
    private bool Current()
    {
        long w=wall(),m=mono();
        if(revoked || !current() || w<latestWall || m<latestMono || m<startedMono || m-startedMono>=1800000 || w/1000>=expiresAt){revoked=true;control.Reset("fixed-phone-unavailable");return false;}
        latestWall=w;latestMono=m;return true;
    }
    private bool Fresh=>!revoked && sequence>0 && mono()<freshUntil;
    private void Synchronize()
    {
        bool? locked=session.IsLocked();
        if(locked!=lastLocked){lastLocked=locked;control.SetSession(locked);ResetArrival();}
        control.Tick(mono()/1000d,Fresh);
        if(control.Presence.State==PresenceState.Far)ResetArrival();
        // Unknown/Uncertain suspends submission through Ready(), but is not a new
        // departure. Do not replace a still-current challenge on every brief gap.
    }
    public FixedPhoneContext GetContext(PollRequest p)
    {
        lock(gate){
            if(!Current())throw new UnauthorizedAccessException("fixed-phone-unavailable");
            long now=wall()/1000;
            if(p==null || p.PcId!=PcId || p.DeviceId!=phone.DeviceId || !Protocol.ValidNonce(p.Nonce) || p.Timestamp<now-30 || p.Timestamp>now+30 || !Protocol.Verify(phone.PublicKey,FixedPhoneWire.ContextPollText(p),p.Signature))throw new UnauthorizedAccessException("fixed-phone-context-invalid");
            foreach(var n in polls.Where(x=>x.Value<now-60).Select(x=>x.Key).ToArray())polls.Remove(n);
            if(polls.Count>=4096 || !polls.TryAdd(p.Nonce,now))throw new UnauthorizedAccessException("fixed-phone-context-replay");
            var result=new FixedPhoneContext(PcId,phone.DeviceId,contextNonce,expiresAt,sequence+1,"");
            return result with{Signature=Protocol.Sign(pc,FixedPhoneWire.ContextText(result))};
        }
    }
    public FixedPhoneResult Observe(BandConnectionReport p)
    {
        lock(gate){
            if(!Current())return new(false,false,"fixed-phone-unavailable");
            long now=wall();
            if(p==null || p.PcId!=PcId || p.DeviceId!=phone.DeviceId || p.ContextNonce!=contextNonce || p.Sequence<=sequence || p.Sequence>9007199254740991 ||
                p.ObservedAtMilliseconds<now-10000 || p.ObservedAtMilliseconds>now+3000 || p.BandHash==null || p.BandHash.Length!=64 || p.BandHash.Any(c=>!char.IsAsciiHexDigitLower(c)) ||
                !Protocol.Verify(phone.PublicKey,FixedPhoneWire.ReportText(p),p.Signature))return new(false,false,"fixed-phone-report-invalid");
            if(bandHash!=null && bandHash!=p.BandHash){Revoke();return new(false,false,"fixed-phone-band-changed");}
            bandHash=p.BandHash;sequence=p.Sequence;reports++;bandConnected=p.Connected;
            freshUntil=mono()+Math.Clamp(p.ObservedAtMilliseconds+10000-now,0,10000);
            bool? locked=session.IsLocked();if(locked!=lastLocked){lastLocked=locked;control.SetSession(locked);ResetArrival();}
            control.ObserveBandConnection(p.Connected,mono()/1000d,Fresh);
            if(control.Presence.State==PresenceState.Far)ResetArrival();
            if(control.CanRequestConfirmation && !arrivalHandled){
                renewalBody=exchange.CreateRequestRenewal();WindowsRequestRenewalWire.Read(renewalBody);
                previousNative=exchange.ReadCurrentChallenge();arrivalHandled=true;submitted=false;readyNative=null;
            }
            RequestRenewalPrompt? prompt=null;
            if(renewalBody!=null && !submitted && WindowsRequestRenewalWire.IsCurrent(WindowsRequestRenewalWire.Read(renewalBody),WindowsGrantRelayWire.FileTimeMilliseconds(now))){
                var unsigned=new RequestRenewalPrompt(PcId,phone.DeviceId,contextNonce,Convert.ToBase64String(renewalBody),"");
                prompt=unsigned with{Signature=Protocol.Sign(pc,FixedPhoneWire.RenewalText(unsigned))};
            }
            bool ready=Ready();return new(true,ready,"fixed-phone-state-accepted",prompt,nativeState);
        }
    }
    private bool Ready()
    {
        try{return ReadyCore();}
        catch(Exception error)when(error is IOException or FormatException or UnauthorizedAccessException){nativeState="native-exchange-error";return false;}
    }
    private bool ReadyCore()
    {
        if(!Fresh){nativeState="state-stale";return false;}
        if(!control.CanRequestConfirmation){nativeState="range-or-session-unconfirmed";return false;}
        if(!submitted || renewalBody==null){nativeState="renewal-not-submitted";return false;}
        var native=exchange.ReadCurrentChallenge();
        if(native==null){nativeState="native-challenge-unavailable";return false;}
        var challenge=WindowsGrantWire.ReadChallenge(native);var renewal=WindowsRequestRenewalWire.Read(renewalBody);
        if(challenge.UserSid!=renewal.UserSid || challenge.WindowsSessionId!=renewal.WindowsSessionId){nativeState="native-context-mismatch";return false;}
        if(challenge.IssuedFileTimeMilliseconds<renewal.IssuedFileTimeMilliseconds){nativeState="native-before-renewal";return false;}
        if(!WindowsGrantWire.IsCurrent(challenge,WindowsGrantRelayWire.FileTimeMilliseconds(wall()))){nativeState="native-expired";return false;}
        if(previousNative!=null && native.AsSpan().SequenceEqual(previousNative)){nativeState="native-unchanged";return false;}
        readyNative??=native.ToArray();bool same=native.AsSpan().SequenceEqual(readyNative);nativeState=same?"ready":"native-replaced";return same;
    }
    public bool CanSubmit {get{lock(gate){if(!Current())return false;Synchronize();return Ready();}}}
    public FixedPhoneResult Renew(RequestRenewalDecision p)
    {
        lock(gate){
            if(!Current())return new(false,false,"fixed-phone-unavailable");Synchronize();
            if(!Fresh || !control.CanRequestConfirmation || renewalBody==null || p==null || p.PcId!=PcId || p.DeviceId!=phone.DeviceId || p.ContextNonce!=contextNonce || p.Body!=Convert.ToBase64String(renewalBody))return new(false,false,"fixed-phone-renewal-context");
            byte[] signature;
            try{signature=Convert.FromBase64String(p.Signature??"");}catch(FormatException){return new(false,false,"fixed-phone-renewal-signature");}
            if(signature.Length!=64)return new(false,false,"fixed-phone-renewal-signature");
            byte[] proof=new byte[WindowsRequestRenewalWire.ProofSize];renewalBody.CopyTo(proof,0);signature.CopyTo(proof,WindowsRequestRenewalWire.Size);
            if(!WindowsRequestRenewalWire.Verify(WindowsRequestRenewalWire.Read(renewalBody),proof,publicKey,WindowsGrantRelayWire.FileTimeMilliseconds(wall())))return new(false,false,"fixed-phone-renewal-signature");
            if(!submitted){submitted=exchange.SubmitRequestRenewal(proof);if(submitted){renewals++;}}
            bool ready=Ready();return new(submitted,ready,submitted?"fixed-phone-renewal-submitted":"fixed-phone-renewal-exchange-unavailable",NativeState:nativeState);
        }
    }
    public FixedPhoneSnapshot Snapshot
    {
        get{lock(gate){if(!Current())return new(null,false,PresenceState.Unknown,reports,renewals,false,true,"revoked");Synchronize();bool ready=Ready();return new(Fresh?bandConnected:null,Fresh,control.Presence.State,reports,renewals,ready,revoked,nativeState);}}
    }
}
