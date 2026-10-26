using System.Security.Cryptography;
using BandKey.Core;

internal static class CarriedPhonePresenceTests
{
    private sealed class Session : IInteractiveSession
    {
        public bool IsInteractive{get;set;}=true;
        public bool? Locked=true;public int Requests;
        public bool? IsLocked()=>Locked;
        public LockRequestResult RequestLock(){Requests++;return new(true,"synthetic-session");}
    }
    private sealed class Fixture : IDisposable
    {
        public readonly ECDsa Phone=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly Session Session=new();
        public long Now=1000;
        public readonly DateTimeOffset Epoch=DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
        public BlePeerBinding? Link;
        public VerifiedPhoneLink? Proof;
        public bool Healthy=true,Current=true;
        public readonly CarriedPhonePresence Presence;
        public string PhoneId=>Protocol.Fingerprint(Protocol.ExportPublicKey(Phone));
        public Fixture(bool autoLock=false,bool missing=false)
        {
            Link=new(new string('a',32),new("AABBCCDDEEFF","Random"),Epoch);
            Presence=new(PhoneId,new(){NearSeconds=1,FarSeconds=3,StaleSeconds=5,MissingSeconds=12,MedianWindow=3,Alpha=1,LockOnMissingSignal=missing},Session,
                ()=>Link,()=>Proof,()=>Healthy,()=>Current,()=>Now,()=>Epoch.AddMilliseconds(Now-1000),autoLock);
        }
        public void Authenticate()=>Proof=new(Link!.ConnectionId,PhoneId,Now,true);
        public DeviceObservation Sample(int rssi=-45)=>new(Link!.Target.Address,"not-an-identity",rssi,Epoch.AddMilliseconds(Now-1000),Link.Target.AddressType!,[]);
        public void Step(int seconds,int rssi=-45,bool authenticate=true)
        {Now=1000+seconds*1000;if(authenticate)Authenticate();Presence.Observe(Sample(rssi));}
        public void Near(){for(int i=0;i<=3;i++)Step(i);}
        public void Dispose()=>Phone.Dispose();
    }
    private sealed class Exchange(byte[] body) : IWindowsGrantExchange
    {
        public int Submissions;
        public byte[]? ReadCurrentChallenge()=>body;
        public bool Submit(ReadOnlySpan<byte> expected,ReadOnlySpan<byte> approval){Submissions++;return true;}
    }
    public static void Run(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("Carried phone presence invariant");}
        test("carried phone ignores strong advertisements without verified link identity",()=>{
            using var f=new Fixture();for(int i=0;i<8;i++)f.Step(i,authenticate:false);
            Check(f.Presence.Tick() is {Authenticated:false,AcceptedSamples:0,CanRequestConfirmation:false});
        });
        test("carried phone needs matching authentication generation and phone fingerprint",()=>{
            using var f=new Fixture();f.Authenticate();f.Proof=f.Proof! with{PhoneId=new string('b',64)};
            Check(!f.Presence.Observe(f.Sample()));f.Proof=f.Proof with{PhoneId=f.PhoneId,ConnectionId=new string('b',32)};
            Check(!f.Presence.Observe(f.Sample()));f.Proof=f.Proof with{ConnectionId=f.Link!.ConnectionId,VerifiedAtMonotonicMilliseconds=f.Now+1};
            Check(!f.Presence.Observe(f.Sample()));
        });
        test("carried phone fresh authenticated near samples open only a locked confirmation gate",()=>{
            using var f=new Fixture();f.Near();Check(f.Presence.Tick() is {CanRequestConfirmation:true,AcceptedSamples:4});
            Check(f.Session.Requests==0);f.Session.Locked=false;Check(!f.Presence.CanRequestConfirmation);
        });
        test("carried phone rejects neighbours address type mismatch anonymous and replayed samples",()=>{
            using var f=new Fixture();f.Authenticate();var s=f.Sample();
            foreach(var sample in new[]{s with{Address="112233445566"},s with{AddressType="Public"},s with{Anonymous=true},s with{Transport="phone-declared-rssi"},s with{Rssi=127},s with{SeenAt=f.Epoch.AddSeconds(-1)},s with{SeenAt=f.Epoch.AddSeconds(1)}})
                Check(!f.Presence.Observe(sample));
            Check(f.Presence.Observe(s));Check(!f.Presence.Observe(s));Check(f.Presence.Tick().AcceptedSamples==1);
        });
        test("carried phone cached authentication and RSSI expire independently",()=>{
            using var f=new Fixture();f.Near();f.Now+=5000;f.Authenticate();Check(!f.Presence.CanRequestConfirmation);
            using var g=new Fixture();g.Near();g.Now+=10000;Check(!g.Presence.CanRequestConfirmation && !g.Presence.Tick().Authenticated);
        });
        test("carried phone band disconnection or unknown status closes confirmation",()=>{
            using var f=new Fixture();f.Near();f.Proof=f.Proof! with{BandConnected=false};Check(!f.Presence.CanRequestConfirmation);
            f.Proof=f.Proof with{BandConnected=null};Check(!f.Presence.CanRequestConfirmation);
        });
        test("carried phone replacement generation requires new identity verification and near dwell",()=>{
            using var f=new Fixture();f.Near();f.Link=f.Link! with{ConnectionId=new string('b',32),Target=new("112233445566","Random")};
            Check(!f.Presence.CanRequestConfirmation);for(int i=4;i<=7;i++)f.Step(i);
            Check(f.Presence.CanRequestConfirmation);
        });
        test("carried phone address mutation cannot retain the same verified generation",()=>{
            using var f=new Fixture();f.Near();var before=f.Link;f.Link=f.Link! with{Target=new("112233445566","Random")};
            Check(!f.Presence.CanRequestConfirmation);f.Link=before;Check(!f.Presence.CanRequestConfirmation);
        });
        test("carried phone link loss stops confirmation and default missing signal does not lock",()=>{
            using var f=new Fixture(autoLock:true);f.Session.Locked=false;f.Near();var before=f.Link;f.Link=null;
            Check(!f.Presence.CanRequestConfirmation);f.Link=before;f.Session.Locked=true;f.Authenticate();Check(!f.Presence.CanRequestConfirmation);
            f.Now+=15000;Check(!f.Presence.CanRequestConfirmation && f.Session.Requests==0);
        });
        test("carried phone explicit missing signal policy locks once after actual link loss",()=>{
            using var f=new Fixture(autoLock:true,missing:true);f.Session.Locked=false;f.Near();f.Link=null;
            f.Now+=5000;f.Presence.Tick();Check(f.Session.Requests==0);
            f.Now+=8000;f.Presence.Tick();f.Now+=1000;f.Presence.Tick();Check(f.Session.Requests==1);
        });
        test("carried phone radio outage is unknown even with missing signal locking enabled",()=>{
            using var f=new Fixture(autoLock:true,missing:true);f.Session.Locked=false;f.Near();f.Healthy=false;f.Presence.Tick();
            f.Now+=20000;f.Healthy=true;f.Authenticate();Check(!f.Presence.CanRequestConfirmation && f.Session.Requests==0);
        });
        test("carried phone cannot request an interactive desktop lock from Session 0",()=>{
            using var f=new Fixture(autoLock:true);f.Session.IsInteractive=false;f.Session.Locked=false;f.Near();
            for(int i=4;i<=15;i++)f.Step(i,-100);Check(f.Session.Requests==0);
        });
        test("carried phone weak sustained RSSI locks once but brief noise does not",()=>{
            using var f=new Fixture(autoLock:true);f.Session.Locked=false;f.Near();f.Step(4,-100);f.Step(5);f.Step(6);Check(f.Session.Requests==0);
            for(int i=7;i<=17;i++)f.Step(i,-100);Check(f.Session.Requests==1);
        });
        test("carried phone resume and manual unlock discard previous readiness",()=>{
            using var f=new Fixture();f.Near();f.Presence.Invalidate("power-transition");f.Authenticate();Check(!f.Presence.CanRequestConfirmation);
            f.Link=f.Link! with{ConnectionId=new string('c',32)};for(int i=4;i<=7;i++)f.Step(i);Check(f.Presence.CanRequestConfirmation);
            f.Session.Locked=false;f.Presence.Tick();f.Session.Locked=true;Check(!f.Presence.CanRequestConfirmation);
        });
        test("carried phone clock rollback and revocation cannot be repaired by cached evidence",()=>{
            using var f=new Fixture();f.Near();f.Now--;Check(!f.Presence.CanRequestConfirmation);f.Now+=2;f.Authenticate();Check(!f.Presence.CanRequestConfirmation);
            using var g=new Fixture();g.Near();g.Presence.Revoke();g.Authenticate();Check(!g.Presence.CanRequestConfirmation);
        });
        foreach(bool expire in new[]{false,true})test(expire?"carried phone signal expiry invalidates a signed pending native decision":"carried phone near requires a separate signed decision before native submission",()=>{
            using var f=new Fixture();f.Near();using var pc=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            long wall=f.Epoch.AddMilliseconds(f.Now-1000).ToUnixTimeMilliseconds();ulong stamp=WindowsGrantRelayWire.FileTimeMilliseconds(wall);
            byte[] body=WindowsGrantWire.WriteChallenge(new(5,stamp,stamp+30000,"S-1-5-21-1-2-3-1001",Protocol.Nonce()));var exchange=new Exchange(body);
            string pub=Protocol.ExportPublicKey(f.Phone);var device=new PublicDevice(f.PhoneId,"synthetic",pub,ConfirmationSources.TestClient,true);
            var relay=new WindowsGrantRelay(pc,device,exchange,()=>true,()=>wall,()=>f.Now,()=>f.Presence.CanRequestConfirmation);
            var poll=new PollRequest(relay.PcId,f.PhoneId,Protocol.Nonce(),wall/1000,"");poll=poll with{Signature=Protocol.Sign(f.Phone,WindowsGrantRelayWire.PollText(poll))};
            var prompt=relay.Poll(poll);Check(prompt!=null && exchange.Submissions==0);byte[] approval=WindowsGrantWire.Sign(WindowsGrantWire.ReadChallenge(body),f.Phone);
            var decision=new WindowsGrantDecision(relay.PcId,f.PhoneId,prompt!.Challenge,true,Convert.ToBase64String(approval.AsSpan(436)));
            if(expire){f.Now+=5000;wall+=5000;f.Authenticate();}
            var result=relay.Decide(decision);Check(result.Submitted==!expire && exchange.Submissions==(expire?0:1) && !result.WindowsUnlocked);
        });
    }
}
