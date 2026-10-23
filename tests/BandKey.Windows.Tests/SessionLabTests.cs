using System.Text.Json;
using BandKey.Core;
using BandKey.SessionLab;
using BandKey.Windows;

internal static class SessionLabTests
{
    private static void Check(bool value){if(!value)throw new Exception("Session lab assertion");}
    private sealed class Session : IInteractiveSession
    {
        public bool IsInteractive{get;set;}=true;
        public bool? Locked=false;
        public int Requests;
        public bool? IsLocked()=>Locked;
        public LockRequestResult RequestLock(){Requests++;return new(true,"submitted");}
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> test)
    {
        await test("session evidence startup unlocked is not an observed unlock",()=>
        {
            var e=new SessionEvidence(false);e.Observe(false,ObservationSource.Query,0);e.Observe(false,ObservationSource.Query,1);
            Check(e.QueryUnlocksAfterLock==0 && e.NotificationUnlocksAfterLock==0 && e.Events.Length==1);return Task.CompletedTask;
        });
        await test("session evidence lagging query cannot turn a native lock into a false unlock",()=>
        {
            var e=new SessionEvidence(false);e.Observe(false,ObservationSource.Query,0);e.Observe(true,ObservationSource.Notification,1);e.Observe(false,ObservationSource.Query,2);
            Check(e.QueryUnlocksAfterLock==0 && e.NotificationUnlocksAfterLock==0);
            e.Observe(false,ObservationSource.Notification,3);Check(e.NotificationUnlocksAfterLock==1 && e.QueryUnlocksAfterLock==0);return Task.CompletedTask;
        });
        await test("session evidence query lock-unlock is recorded once despite repeated samples",()=>
        {
            var e=new SessionEvidence(false);e.Observe(true,ObservationSource.Query,0);e.Observe(true,ObservationSource.Query,1);e.Observe(false,ObservationSource.Query,2);e.Observe(false,ObservationSource.Query,3);
            Check(e.QueryUnlocksAfterLock==1 && e.Events.Length==2);return Task.CompletedTask;
        });
        await test("session evidence unknown breaks the query observation chain",()=>
        {
            var e=new SessionEvidence(false);e.Observe(true,ObservationSource.Query,0);e.Observe(null,ObservationSource.Query,1);e.Observe(false,ObservationSource.Query,2);
            Check(e.QueryUnlocksAfterLock==0);return Task.CompletedTask;
        });
        await test("session evidence preview never claims actual Windows observation or attribution",()=>
        {
            var e=new SessionEvidence(true);e.Observe(true,ObservationSource.Notification,0);e.Observe(false,ObservationSource.Notification,1);
            var report=JsonSerializer.SerializeToElement(e.Report());Check(!report.GetProperty("actualWindowsObservation").GetBoolean() && !report.GetProperty("unlockAttributedToBandKey").GetBoolean());return Task.CompletedTask;
        });
        await test("session evidence rejects invalid time or source before recording",()=>
        {
            var e=new SessionEvidence(false);e.Observe(false,ObservationSource.Query,2);
            foreach(var action in new Action[]{()=>e.Observe(true,ObservationSource.Query,double.NaN),()=>e.Observe(true,ObservationSource.Query,1),()=>e.Observe(true,(ObservationSource)99,3)})
            {bool refused=false;try{action();}catch(ArgumentException){refused=true;}Check(refused);}
            Check(e.Events.Length==1);return Task.CompletedTask;
        });
        await test("session evidence bounds events and stops new observation claims on truncation",()=>
        {
            var e=new SessionEvidence(false);for(int i=0;i<512;i++)e.Observe(i%2==0,ObservationSource.Query,i);
            int previous=e.QueryUnlocksAfterLock;e.Observe(true,ObservationSource.Query,512);e.Observe(false,ObservationSource.Query,513);
            Check(e.Truncated && e.Events.Length==512 && e.QueryUnlocksAfterLock==previous);return Task.CompletedTask;
        });
        await test("session evidence lock request does not become an observed lock and redacts unknown text",()=>
        {
            var e=new SessionEvidence(false);e.Request(new(true,"TEST_PRIVATE_MESSAGE"),0);
            Check(e.Events.Length==0 && e.Attempts[0].Code=="request-failed" && !JsonSerializer.Serialize(e.Report()).Contains("TEST_PRIVATE_MESSAGE"));return Task.CompletedTask;
        });
        await test("session lab guest rejection cannot reach the real lock adapter",()=>
        {
            var session=new Session();var guarded=new GuestOnlySession(session,()=>throw new InvalidOperationException("test denial"));
            Check(!guarded.RequestLock().Submitted && session.Requests==0);return Task.CompletedTask;
        });
        await test("session lab noninteractive, locked and unknown contexts fail closed",()=>
        {
            foreach(var session in new[]{new Session{IsInteractive=false},new Session{Locked=true},new Session{Locked=null}})
            {var guarded=new GuestOnlySession(session,()=>{});Check(!guarded.RequestLock().Submitted && session.Requests==0);}return Task.CompletedTask;
        });
        await test("session lab permitted unlocked VM delegates one explicit request",()=>
        {
            var session=new Session();int checks=0;var guarded=new GuestOnlySession(session,()=>checks++);
            Check(guarded.RequestLock().Submitted && session.Requests==1 && checks==1);return Task.CompletedTask;
        });
        await test("session lab simulated jitter never requests a desktop lock",()=>
        {
            var session=new Session();var run=new SessionLabRun(session,true);Check(run.StartScenario(SignalScenario.Jitter,0));
            for(int i=0;i<=30;i++){run.Observe(false,ObservationSource.Query,i*1000);run.Tick(i*1000);}
            Check(session.Requests==0 && run.Scenario==SignalScenario.None && run.ScenarioEverStarted);return Task.CompletedTask;
        });
        await test("session lab simulated departure requests once but does not invent WTS confirmation",()=>
        {
            var session=new Session();var run=new SessionLabRun(session,true);Check(run.StartScenario(SignalScenario.Away,0));
            for(int i=0;i<=35;i++){run.Observe(false,ObservationSource.Query,i*1000);run.Tick(i*1000);}
            Check(session.Requests==1 && run.Evidence.Attempts.Length==1 && run.Evidence.Events.All(e=>e.Locked==false) && run.Scenario==SignalScenario.None);return Task.CompletedTask;
        });
        await test("session lab observed lock-unlock cancels the old scenario without auto-rearming",()=>
        {
            var session=new Session();var run=new SessionLabRun(session,true);Check(run.StartScenario(SignalScenario.Away,0));
            run.Observe(true,ObservationSource.Notification,1000);run.Observe(false,ObservationSource.Notification,2000);
            for(int i=3;i<35;i++)run.Tick(i*1000);
            Check(session.Requests==0 && run.Scenario==SignalScenario.None && run.Evidence.NotificationUnlocksAfterLock==1);return Task.CompletedTask;
        });
        await test("session lab stop cancels pending simulated departure",()=>
        {
            var session=new Session();var run=new SessionLabRun(session,true);run.StartScenario(SignalScenario.Away,0);run.Tick(0);run.StopScenario();
            for(int i=1;i<35;i++)run.Tick(i*1000);Check(session.Requests==0);return Task.CompletedTask;
        });
        await test("session lab cannot arm from unknown state or a nonfinite clock",()=>
        {
            var session=new Session{Locked=null};var run=new SessionLabRun(session,true);Check(!run.StartScenario(SignalScenario.Away,0));
            bool refused=false;try{run.StartScenario(SignalScenario.Away,double.NaN);}catch(ArgumentException){refused=true;}
            Check(refused && session.Requests==0);return Task.CompletedTask;
        });
        await test("session lab request log exhaustion prevents another real lock attempt",()=>
        {
            var session=new Session();var run=new SessionLabRun(session,true);
            for(int i=0;i<16;i++)run.Evidence.Request(new(false,"preview-disabled"),i);
            Check(!run.StartScenario(SignalScenario.Away,20) && session.Requests==0);return Task.CompletedTask;
        });
    }
}
