using System.Globalization;
using System.Security.Cryptography;

namespace BandKey.Core;

public sealed record FixedPhoneContext(string PcId,string DeviceId,string ContextNonce,long ExpiresAt,long NextSequence,string Signature);
public sealed record BandConnectionReport(string PcId,string DeviceId,string ContextNonce,long Sequence,long ObservedAtMilliseconds,string BandHash,bool? Connected,string Signature,PollRequest? Poll=null);
public sealed record RequestRenewalPrompt(string PcId,string DeviceId,string ContextNonce,string Body,string Signature);
public sealed record RequestRenewalDecision(string PcId,string DeviceId,string ContextNonce,string Body,string Signature,PollRequest? Poll=null);
public sealed record FixedPhoneResult(bool Accepted,bool CanPoll,string Code,RequestRenewalPrompt? Renewal=null,string? NativeState=null,WindowsGrantPrompt? Prompt=null)
{ public bool WindowsUnlocked=>false; public bool TestOnly=>true; }
public sealed record FixedPhoneSnapshot(bool? BandConnected,bool Fresh,PresenceState Presence,long Reports,long Renewals,bool NativeRequestReady,bool Revoked,string NativeState="not-requested",bool DirectRequest=false,int ReplacementRecoveries=0,long ConnectedReports=0,long DisconnectedReports=0,long UnknownReports=0,bool AutoLockEnabled=false,bool DepartureArmed=false,string PresenceReason="not-observed",long LockRequests=0,string LastLockResult="not-requested",long PhoneChannelReturns=0,int RenewalRecoveries=0);
public sealed record WindowsRequestBinding(string UserSid,uint WindowsSessionId);

public interface IWindowsRequestRenewalExchange
{
    WindowsRequestBinding? Binding=>null;
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
    private readonly bool useFreshLockRequest;
    private long lastUnlockedWall=long.MinValue,fastWaitUntil;
    private bool freshLockEligible,directRequest,replacementPending;
    private int replacementRecoveries;
    private const int MaxReplacementRecoveries=2;
    private int renewalRecoveries;
    private const int MaxRenewalRecoveries=3;
    private long connectedReports,disconnectedReports,unknownReports,lockRequests;
    private readonly bool renewOnAuthenticatedReturn;
    private long lastAcceptedReportMono,phoneChannelReturns;
    private string lastLockResult="not-requested";
    public string PcId{get;}
    public FixedPhoneChannel(ECDsa pc,PublicDevice phone,IWindowsRequestRenewalExchange exchange,IInteractiveSession session,Func<bool> current,
        bool autoLock=false,Func<long>? wall=null,Func<long>? mono=null,PresenceOptions? options=null,bool useFreshLockRequest=false,bool renewOnAuthenticatedReturn=false)
    {
        if(!phone.TestOnly || phone.ConfirmationSource!=ConfirmationSources.TestClient || phone.DeviceId!=Protocol.Fingerprint(phone.PublicKey))throw new ArgumentException("Explicit VM phone enrollment required");
        using var key=Protocol.ReadPublicKey(phone.PublicKey);publicKey=WindowsGrantWire.ExportNativePublicKey(key);
        this.pc=pc;this.phone=phone;this.exchange=exchange;this.session=session;this.current=current;
        this.wall=wall??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());this.mono=mono??(()=>Environment.TickCount64);
        this.useFreshLockRequest=useFreshLockRequest;
        this.renewOnAuthenticatedReturn=renewOnAuthenticatedReturn;
        startedMono=this.mono();expiresAt=checked(this.wall()/1000+1800);PcId=Protocol.Fingerprint(Protocol.ExportPublicKey(pc));
        control=new(options??new PresenceOptions{NearSeconds=1},session){Enabled=autoLock};control.SelectInputMode(ProximityInputMode.FixedPhoneBandConnection);
        control.LockAttempted+=result=>{lockRequests++;lastLockResult=result.Code;};
    }
    public void Revoke(){lock(gate){revoked=true;ResetArrival();control.Reset("fixed-phone-revoked");}}
    public void RetryRequest(){lock(gate){ResetArrival();}}
    private void ClearRequest(){arrivalHandled=submitted=directRequest=replacementPending=false;renewalBody=previousNative=readyNative=null;}
    private void ResetArrival(){ClearRequest();freshLockEligible=false;fastWaitUntil=0;replacementRecoveries=renewalRecoveries=0;}
    private void ObserveSession(bool? locked,bool previousEvidenceFresh)
    {
        // Check the old evidence before a new report or lock edge can inherit it.
        control.Tick(mono()/1000d,Fresh);
        if(locked!=lastLocked){
            bool keep=lastLocked==false && locked==true && previousEvidenceFresh && bandConnected==true && control.Presence.State==PresenceState.Near;
            bool fast=keep && useFreshLockRequest && lastUnlockedWall!=long.MinValue && exchange.Binding!=null;
            lastLocked=locked;control.SetSession(locked,keep);ResetArrival();
            if(fast){freshLockEligible=true;fastWaitUntil=mono()+1000;}
        }
        if(locked==false)lastUnlockedWall=wall();
    }
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
        ObserveSession(locked,Fresh && bandConnected==true);
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
            // A recovered authenticated phone channel is a return hint, not an
            // RSSI/range claim and never an approval. Preserve the VM default.
            // Require a new valid state proof after at least 15 seconds without
            // one; repeated old reports cannot manufacture a return event.
            if(renewOnAuthenticatedReturn && sequence>0 && mono()-lastAcceptedReportMono>=15000){
                ResetArrival();control.Reset("authenticated-phone-channel-return");phoneChannelReturns++;
            }
            lastAcceptedReportMono=mono();
            bool previousEvidenceFresh=Fresh && bandConnected==true;bandHash=p.BandHash;sequence=p.Sequence;reports++;bandConnected=p.Connected;
            if(p.Connected==true)connectedReports++;else if(p.Connected==false)disconnectedReports++;else unknownReports++;
            freshUntil=mono()+Math.Clamp(p.ObservedAtMilliseconds+10000-now,0,10000);
            ObserveSession(session.IsLocked(),previousEvidenceFresh);
            control.ObserveBandConnection(p.Connected,mono()/1000d,Fresh);
            if(control.Presence.State==PresenceState.Far)ResetArrival();
            if(arrivalHandled)Ready();
            RecoverReplacement();
            RecoverMissedRenewal();
            if(control.CanRequestConfirmation && !arrivalHandled){
                bool wait=false;
                if(!TryFreshLockRequest(out wait) && !wait){
                    renewalBody=exchange.CreateRequestRenewal();WindowsRequestRenewalWire.Read(renewalBody);
                    previousNative=exchange.ReadCurrentChallenge();arrivalHandled=true;submitted=false;readyNative=null;freshLockEligible=false;
                }
            }
            RequestRenewalPrompt? prompt=null;
            if(renewalBody!=null && !submitted && WindowsRequestRenewalWire.IsCurrent(WindowsRequestRenewalWire.Read(renewalBody),WindowsGrantRelayWire.FileTimeMilliseconds(now))){
                var unsigned=new RequestRenewalPrompt(PcId,phone.DeviceId,contextNonce,Convert.ToBase64String(renewalBody),"");
                prompt=unsigned with{Signature=Protocol.Sign(pc,FixedPhoneWire.RenewalText(unsigned))};
            }
            bool ready=Ready();return new(true,ready,"fixed-phone-state-accepted",prompt,nativeState);
        }
    }
    private bool TryFreshLockRequest(out bool wait)
    {
        wait=false;if(!freshLockEligible)return false;
        var native=exchange.ReadCurrentChallenge();var binding=exchange.Binding;
        if(native!=null && binding!=null){
            var c=WindowsGrantWire.ReadChallenge(native);ulong now=WindowsGrantRelayWire.FileTimeMilliseconds(wall());
            if(c.UserSid==binding.UserSid && c.WindowsSessionId==binding.WindowsSessionId &&
                c.IssuedFileTimeMilliseconds>WindowsGrantRelayWire.FileTimeMilliseconds(lastUnlockedWall) &&
                WindowsGrantWire.IsCurrent(c,now) && c.ExpiresFileTimeMilliseconds-now>=10000){
                readyNative=native.ToArray();directRequest=arrivalHandled=true;freshLockEligible=false;nativeState="ready-direct";return true;
            }
        }
        wait=mono()<fastWaitUntil;if(wait)nativeState="waiting-first-native";return false;
    }
    private void RecoverReplacement()
    {
        if(!replacementPending || !control.CanRequestConfirmation)return;
        if(replacementRecoveries>=MaxReplacementRecoveries){nativeState="native-replacement-limit";return;}
        var native=exchange.ReadCurrentChallenge();if(native==null)return;
        var c=WindowsGrantWire.ReadChallenge(native);var binding=exchange.Binding;
        if(binding==null && renewalBody!=null){var r=WindowsRequestRenewalWire.Read(renewalBody);binding=new(r.UserSid,r.WindowsSessionId);}
        if(binding==null || c.UserSid!=binding.UserSid || c.WindowsSessionId!=binding.WindowsSessionId ||
            !WindowsGrantWire.IsCurrent(c,WindowsGrantRelayWire.FileTimeMilliseconds(wall())))return;
        replacementRecoveries++;ClearRequest();freshLockEligible=false;
        // A replacement requires a new NEW proof and a new native WIN challenge.
        // The phone invalidates its old notification on the accepted renewal count.
    }
    private void RecoverMissedRenewal()
    {
        // A return after WIN expiry needs a separate ten-second NEW proof. Losing
        // that exchange must not latch this arrival forever. Reissue only NEW;
        // an observed WIN (including a denied/expired notification) is never renewed here.
        if(!renewOnAuthenticatedReturn || !arrivalHandled || directRequest || readyNative!=null || renewalBody==null ||
            !Fresh || !control.CanRequestConfirmation || renewalRecoveries>=MaxRenewalRecoveries ||
            nativeState is not ("renewal-not-submitted" or "native-challenge-unavailable" or "native-unchanged" or "native-before-renewal"))return;
        var request=WindowsRequestRenewalWire.Read(renewalBody);
        if(WindowsGrantRelayWire.FileTimeMilliseconds(wall())<request.ExpiresFileTimeMilliseconds)return;
        renewalRecoveries++;ClearRequest();freshLockEligible=false;
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
        if(replacementPending){nativeState=replacementRecoveries>=MaxReplacementRecoveries?"native-replacement-limit":"native-replaced";return false;}
        if(!directRequest && (!submitted || renewalBody==null)){nativeState=freshLockEligible?"waiting-first-native":"renewal-not-submitted";return false;}
        var native=exchange.ReadCurrentChallenge();
        if(native==null){nativeState="native-challenge-unavailable";return false;}
        var challenge=WindowsGrantWire.ReadChallenge(native);
        if(directRequest){
            var binding=exchange.Binding;
            if(binding==null || challenge.UserSid!=binding.UserSid || challenge.WindowsSessionId!=binding.WindowsSessionId ||
                challenge.IssuedFileTimeMilliseconds<=WindowsGrantRelayWire.FileTimeMilliseconds(lastUnlockedWall)){nativeState="native-context-mismatch";return false;}
        }else{
            var renewal=WindowsRequestRenewalWire.Read(renewalBody!);
            if(challenge.UserSid!=renewal.UserSid || challenge.WindowsSessionId!=renewal.WindowsSessionId){nativeState="native-context-mismatch";return false;}
            if(challenge.IssuedFileTimeMilliseconds<renewal.IssuedFileTimeMilliseconds){nativeState="native-before-renewal";return false;}
        }
        if(!WindowsGrantWire.IsCurrent(challenge,WindowsGrantRelayWire.FileTimeMilliseconds(wall()))){nativeState="native-expired";return false;}
        if(previousNative!=null && native.AsSpan().SequenceEqual(previousNative)){nativeState="native-unchanged";return false;}
        readyNative??=native.ToArray();bool same=native.AsSpan().SequenceEqual(readyNative);
        if(!same)replacementPending=true;
        nativeState=same?(directRequest?"ready-direct":"ready"):(replacementRecoveries>=MaxReplacementRecoveries?"native-replacement-limit":"native-replaced");return same;
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
        get{lock(gate){if(!Current())return new(null,false,PresenceState.Unknown,reports,renewals,false,true,"revoked",ConnectedReports:connectedReports,DisconnectedReports:disconnectedReports,UnknownReports:unknownReports,AutoLockEnabled:control.Enabled,PresenceReason:"revoked",LockRequests:lockRequests,LastLockResult:lastLockResult,PhoneChannelReturns:phoneChannelReturns,RenewalRecoveries:renewalRecoveries);Synchronize();bool ready=Ready();return new(Fresh?bandConnected:null,Fresh,control.Presence.State,reports,renewals,ready,revoked,nativeState,directRequest,replacementRecoveries,connectedReports,disconnectedReports,unknownReports,control.Enabled,control.Presence.Armed,control.Presence.Reason,lockRequests,lastLockResult,phoneChannelReturns,renewalRecoveries);}}
    }
}
