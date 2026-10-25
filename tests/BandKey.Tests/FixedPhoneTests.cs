using BandKey.Core;
using System.Security.Cryptography;

internal static class FixedPhoneTests
{
    private const string Sid="S-1-5-21-1-2-3-1001";
    private sealed class Desktop:IInteractiveSession
    {
        public bool IsInteractive=>true;public bool? Locked=true;public int Locks;
        public bool? IsLocked()=>Locked;
        public LockRequestResult RequestLock(){Locks++;return new(true,"submitted");}
    }
    private sealed class Exchange:IWindowsRequestRenewalExchange
    {
        public Func<long> Wall=()=>0;public byte[]? Native,Proof;public int Submissions;
        public byte[] CreateRequestRenewal(){ulong now=WindowsGrantRelayWire.FileTimeMilliseconds(Wall());return WindowsRequestRenewalWire.Write(new(5,now,now+10000,Sid,Protocol.Nonce()));}
        public bool SubmitRequestRenewal(ReadOnlySpan<byte> proof){Proof=proof.ToArray();Submissions++;return true;}
        public byte[]? ReadCurrentChallenge()=>Native?.ToArray();
        public void NewNative(){ulong now=WindowsGrantRelayWire.FileTimeMilliseconds(Wall());Native=WindowsGrantWire.WriteChallenge(new(5,now,now+30000,Sid,Protocol.Nonce()));}
    }
    private sealed class Fixture:IDisposable
    {
        public readonly ECDsa Pc=ECDsa.Create(ECCurve.NamedCurves.nistP256),Phone=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly Desktop Session=new();public readonly Exchange Exchange=new();public readonly PublicDevice Device;
        public readonly FixedPhoneChannel Channel;public FixedPhoneContext Context;public long Wall=1700000000000,Mono=1000,Sequence;
        public Fixture(bool autoLock=false){
            string key=Protocol.ExportPublicKey(Phone);Device=new(Protocol.Fingerprint(key),"VM test",key,ConfirmationSources.TestClient,true);
            Exchange.Wall=()=>Wall;
            Channel=new(Pc,Device,Exchange,Session,()=>true,autoLock,()=>Wall,()=>Mono,new(){NearSeconds=1,FarSeconds=3,StaleSeconds=5,MissingSeconds=12});
            Context=Channel.GetContext(Poll());
        }
        public PollRequest Poll(){var p=new PollRequest(Channel.PcId,Device.DeviceId,Protocol.Nonce(),Wall/1000,"");return p with{Signature=Protocol.Sign(Phone,FixedPhoneWire.ContextPollText(p))};}
        public BandConnectionReport Report(bool? connected,string? hash=null){
            var p=new BandConnectionReport(Channel.PcId,Device.DeviceId,Context.ContextNonce,++Sequence,Wall,hash??new string('a',64),connected,"");
            return p with{Signature=Protocol.Sign(Phone,FixedPhoneWire.ReportText(p))};
        }
        public void Advance(long ms){Wall+=ms;Mono+=ms;}
        public RequestRenewalPrompt Near(){Channel.Observe(Report(true));Advance(1000);return Channel.Observe(Report(true)).Renewal??throw new Exception("Renewal missing");}
        public RequestRenewalDecision ApproveRenewal(RequestRenewalPrompt p){
            var body=Convert.FromBase64String(p.Body);var c=WindowsRequestRenewalWire.Read(body);var signed=WindowsRequestRenewalWire.Sign(c,Phone);
            return new(p.PcId,p.DeviceId,p.ContextNonce,p.Body,Convert.ToBase64String(signed.AsSpan(436)));
        }
        public void Dispose(){Pc.Dispose();Phone.Dispose();}
    }
    public static void Run(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("Fixed phone invariant");}
        void Refuses(Action a){try{a();}catch(FormatException){return;}catch(UnauthorizedAccessException){return;}throw new Exception("Expected refusal");}
        test("fixed-phone status stays readable after monotonic clock rollback revokes the run",()=>{
            using var f=new Fixture();f.Near();f.Mono=0;Check(!f.Channel.CanSubmit);var snapshot=f.Channel.Snapshot;
            Check(snapshot.Revoked && !snapshot.Fresh && snapshot.Presence==PresenceState.Unknown && !snapshot.NativeRequestReady);
        });
        test("transient unknown pauses approval without replacing the current native challenge",()=>{
            using var f=new Fixture();var p=f.Near();f.Channel.Renew(f.ApproveRenewal(p));f.Exchange.NewNative();Check(f.Channel.CanSubmit);
            f.Advance(1000);f.Channel.Observe(f.Report(null));Check(!f.Channel.CanSubmit);
            f.Advance(1000);Check(f.Channel.Observe(f.Report(true)).Renewal==null);
            f.Advance(1000);Check(f.Channel.Observe(f.Report(true)).Renewal==null && f.Channel.CanSubmit && f.Exchange.Submissions==1);
        });
        test("heartbeat gap cannot repeatedly renew an already handled arrival",()=>{
            using var f=new Fixture();var p=f.Near();f.Channel.Renew(f.ApproveRenewal(p));f.Exchange.NewNative();
            for(int i=0;i<3;i++){f.Advance(6000);Check(!f.Channel.CanSubmit);f.Channel.Observe(f.Report(true));f.Advance(1000);Check(f.Channel.Observe(f.Report(true)).Renewal==null);}
            Check(f.Exchange.Submissions==1 && f.Channel.Snapshot.Renewals==1);
        });
        test("brief false status does not churn a pending request before departure dwell",()=>{
            using var f=new Fixture();var p=f.Near();f.Channel.Renew(f.ApproveRenewal(p));f.Exchange.NewNative();
            f.Advance(1);f.Channel.Observe(f.Report(false));Check(!f.Channel.CanSubmit);
            f.Advance(1000);f.Channel.Observe(f.Report(true));f.Advance(1000);
            Check(f.Channel.Observe(f.Report(true)).Renewal==null && f.Channel.CanSubmit && f.Exchange.Submissions==1);
        });
        test("unknown recovery never extends expired authorization or auto-renews it",()=>{
            using var f=new Fixture();var p=f.Near();f.Channel.Renew(f.ApproveRenewal(p));f.Exchange.NewNative();
            f.Advance(31000);Check(!f.Channel.CanSubmit);f.Channel.Observe(f.Report(true));f.Advance(1000);
            Check(f.Channel.Observe(f.Report(true)).Renewal==null && !f.Channel.CanSubmit);
            f.Channel.RetryRequest();Check(f.Channel.Observe(f.Report(true)).Renewal!=null);
        });
        test("fixed-phone context is PC-signed and poll replay is rejected",()=>{
            using var f=new Fixture();Check(Protocol.Verify(Protocol.ExportPublicKey(f.Pc),FixedPhoneWire.ContextText(f.Context),f.Context.Signature));
            var poll=f.Poll();f.Channel.GetContext(poll);Refuses(()=>f.Channel.GetContext(poll));
        });
        test("signed connected dwell creates a renewal but never an unlock approval",()=>{
            using var f=new Fixture();var p=f.Near();Check(Protocol.Verify(Protocol.ExportPublicKey(f.Pc),FixedPhoneWire.RenewalText(p),p.Signature));
            var body=Convert.FromBase64String(p.Body);Refuses(()=>WindowsGrantWire.ReadChallenge(body));Check(!f.Channel.CanSubmit && f.Exchange.Submissions==0 && f.Session.Locked==true);
        });
        test("renewal proof cannot cross into Windows unlock authorization domain",()=>{
            using var f=new Fixture();var p=f.Near();var r=WindowsRequestRenewalWire.Read(Convert.FromBase64String(p.Body));var proof=WindowsRequestRenewalWire.Sign(r,f.Phone);
            Check(WindowsRequestRenewalWire.Verify(r,proof,WindowsGrantWire.ExportNativePublicKey(f.Phone),r.IssuedFileTimeMilliseconds));
            var auth=r with{ExpiresFileTimeMilliseconds=r.IssuedFileTimeMilliseconds+30000};
            Check(!WindowsGrantWire.Verify(auth,proof,WindowsGrantWire.ExportNativePublicKey(f.Phone),r.IssuedFileTimeMilliseconds));
        });
        test("signed renewal waits for a fresh native challenge before normal polling",()=>{
            using var f=new Fixture();f.Exchange.NewNative();var old=f.Exchange.Native!.ToArray();var p=f.Near();var result=f.Channel.Renew(f.ApproveRenewal(p));
            Check(result.Accepted && !result.CanPoll && !result.WindowsUnlocked && f.Exchange.Submissions==1 && f.Session.Locked==true);
            f.Advance(1);f.Exchange.NewNative();Check(!f.Exchange.Native!.SequenceEqual(old) && f.Channel.CanSubmit && f.Channel.Snapshot.NativeRequestReady);
        });
        test("renewal submission is idempotent and does not reread Windows credentials",()=>{
            using var f=new Fixture();var p=f.Near();var d=f.ApproveRenewal(p);f.Channel.Renew(d);f.Channel.Renew(d);Check(f.Exchange.Submissions==1 && f.Channel.Snapshot.Renewals==1);
        });
        test("fixed-phone state signature and sequence reject tampering and replay",()=>{
            using var f=new Fixture();var p=f.Report(true);Check(!f.Channel.Observe(p with{Connected=false}).Accepted);Check(f.Channel.Observe(p).Accepted);Check(!f.Channel.Observe(p).Accepted && f.Channel.Snapshot.Reports==1);
        });
        test("fixed-phone messages cannot enter a new PC run",()=>{
            using var a=new Fixture();using var b=new Fixture();Check(!b.Channel.Observe(a.Report(true)).Accepted);
        });
        test("changing the signed selected band revokes the fixed-phone run",()=>{
            using var f=new Fixture();f.Channel.Observe(f.Report(true));Check(!f.Channel.Observe(f.Report(true,new string('b',64))).Accepted && f.Channel.Snapshot.Revoked);
        });
        test("stale band reports and heartbeat disappearance cannot generate requests",()=>{
            using var f=new Fixture();var report=f.Report(true);f.Advance(10001);Check(!f.Channel.Observe(report).Accepted);
            f.Channel.Observe(f.Report(true));f.Advance(10001);Check(!f.Channel.Snapshot.Fresh && f.Channel.Snapshot.BandConnected==null && f.Exchange.Submissions==0);
        });
        test("band departure rejects an already prepared renewal proof",()=>{
            using var f=new Fixture();var p=f.Near();f.Advance(1);f.Channel.Observe(f.Report(false));Check(!f.Channel.Renew(f.ApproveRenewal(p)).Accepted && !f.Channel.CanSubmit && f.Exchange.Submissions==0);
        });
        test("expired or revoked renewal cannot create a new request",()=>{
            using var f=new Fixture();var p=f.Near();var d=f.ApproveRenewal(p);f.Advance(10000);Check(!f.Channel.Renew(d).Accepted);f.Channel.Revoke();Check(!f.Channel.Observe(f.Report(true)).Accepted);
        });
        test("return creates a new renewal after old grant expiry without replaying the old nonce",()=>{
            using var f=new Fixture();var first=f.Near();f.Channel.Renew(f.ApproveRenewal(first));f.Exchange.NewNative();
            f.Advance(1);f.Channel.Observe(f.Report(false));f.Advance(3000);f.Channel.Observe(f.Report(false));f.Advance(60000);f.Channel.Observe(f.Report(false));f.Advance(1000);f.Channel.Observe(f.Report(true));f.Advance(1000);
            var next=f.Channel.Observe(f.Report(true)).Renewal;Check(next!=null && next.Body!=first.Body && !f.Channel.Renew(f.ApproveRenewal(first)).Accepted);
        });
        test("default fixed-phone channel never auto-locks and explicit mode uses delayed disconnect",()=>{
            using var noLock=new Fixture();noLock.Session.Locked=false;noLock.Channel.Observe(noLock.Report(true));noLock.Advance(1000);noLock.Channel.Observe(noLock.Report(true));
            noLock.Advance(1000);noLock.Channel.Observe(noLock.Report(false));noLock.Advance(3000);noLock.Channel.Observe(noLock.Report(false));Check(noLock.Session.Locks==0);
            using var enabled=new Fixture(true);enabled.Session.Locked=false;enabled.Channel.Observe(enabled.Report(true));enabled.Advance(1000);enabled.Channel.Observe(enabled.Report(true));
            enabled.Advance(1000);enabled.Channel.Observe(enabled.Report(false));enabled.Advance(3000);enabled.Channel.Observe(enabled.Report(false));Check(enabled.Session.Locks==1);
        });
    }
}
