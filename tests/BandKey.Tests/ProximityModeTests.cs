using BandKey.Core;

internal static class ProximityModeTests
{
    private sealed class Session : IInteractiveSession
    {
        public bool IsInteractive { get; set; } = true;
        public int Requests;
        public bool? IsLocked() => false;
        public LockRequestResult RequestLock() { Requests++; return new(true, "submitted"); }
    }
    private static PresenceOptions Options() => new() { NearSeconds=1,FarSeconds=3,StaleSeconds=5,MissingSeconds=12,MedianWindow=3,Alpha=1 };
    public static void Run(Action<string,Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Proximity mode invariant"); }
        void Refuses(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Expected invalid input rejection"); }
        AutoLockController Controller(Session session,ProximityInputMode mode)
        { var c=new AutoLockController(Options(),session){Enabled=true};c.SelectInputMode(mode);c.SetSession(false);return c; }
        void Connected(AutoLockController c) { c.ObserveBandConnection(true,0,true);c.ObserveBandConnection(true,1,true); }
        test("fixed-phone startup disconnected never locks",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);
            for(int t=0;t<20;t++)c.ObserveBandConnection(false,t,true);Check(s.Requests==0 && c.Presence.State==PresenceState.Far && !c.Presence.Armed);
        });
        test("fixed-phone stable band connection followed by departure locks once",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);
            for(int t=2;t<20;t++)c.ObserveBandConnection(false,t,true);Check(s.Requests==1 && c.Presence.FilteredRssi==null);
        });
        test("brief band disconnect cannot lock",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);
            c.ObserveBandConnection(false,2,true);c.ObserveBandConnection(true,3,true);c.ObserveBandConnection(true,4,true);Check(s.Requests==0);
        });
        test("phone heartbeat loss is unknown rather than band departure",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);
            c.Tick(10,true);Check(s.Requests==0 && c.Presence.State==PresenceState.Unknown && !c.Presence.Armed);
            for(int t=11;t<20;t++)c.ObserveBandConnection(false,t,true);Check(s.Requests==0);
        });
        test("unhealthy or unknown band source disarms",()=>{
            var m=new BandConnectionMonitor(Options());m.Observe(true,0,true);m.Observe(true,1,true);
            Check(!m.Observe(null,2,true).Armed);m.Observe(true,3,true);m.Observe(true,4,true);Check(!m.Observe(false,5,false).Armed);
        });
        test("returning band enables confirmation only after fresh connected dwell",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);c.SetSession(true);
            c.ObserveBandConnection(false,2,true);c.ObserveBandConnection(false,5,true);Check(!c.CanRequestConfirmation);
            c.ObserveBandConnection(true,6,true);Check(!c.CanRequestConfirmation);c.ObserveBandConnection(true,7,true);Check(c.CanRequestConfirmation && s.Requests==0);
        });
        test("fixed-phone mode refuses stationary phone RSSI as band evidence",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);
            Refuses(()=>c.ObserveCarriedPhone(-40,0,true));Refuses(()=>c.Observe(-40,0,true));Check(c.Presence.State==PresenceState.Unknown && s.Requests==0);
        });
        test("switching modes discards previous presence and lock readiness",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);
            c.SelectInputMode(ProximityInputMode.CarriedPhoneRssi);Check(!c.Presence.Armed && c.Presence.State==PresenceState.Unknown);
            Refuses(()=>c.ObserveBandConnection(false,2,true));for(int t=2;t<12;t++)c.ObserveCarriedPhone(-100,t,true);Check(s.Requests==0);
        });
        test("carried-phone mode reuses filtered RSSI departure policy",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.CarriedPhoneRssi);
            for(int t=0;t<=4;t++)c.ObserveCarriedPhone(-45,t,true);
            for(int t=5;t<20;t++)c.ObserveCarriedPhone(-100,t,true);Check(s.Requests==1 && c.Presence.FilteredRssi!=null);
        });
        test("fixed-phone path does not lock from Session 0",()=>{
            var s=new Session{IsInteractive=false};var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);
            for(int t=2;t<10;t++)c.ObserveBandConnection(false,t,true);Check(s.Requests==0);
        });
        test("connection observations require continuous samples and monotonic time",()=>{
            var m=new BandConnectionMonitor(Options());m.Observe(true,0,true);Check(!m.Observe(true,10,true).Armed);
            Refuses(()=>m.Observe(false,9,true));Refuses(()=>m.Tick(double.NaN,true));
        });
        test("manual unlock requires a new stable connection before departure",()=>{
            var s=new Session();var c=Controller(s,ProximityInputMode.FixedPhoneBandConnection);Connected(c);c.SetSession(true);c.SetSession(false);
            for(int t=2;t<10;t++)c.ObserveBandConnection(false,t,true);Check(s.Requests==0 && !c.CanRequestConfirmation);
        });
    }
}
