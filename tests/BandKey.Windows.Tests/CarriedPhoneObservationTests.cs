using BandKey.Core;
using BandKey.Windows;

// Injected scanner and authenticated-session evidence; no physical radio or login APIs.
internal static class CarriedPhoneObservationTests
{
    private sealed class Scanner : IDeviceObservationSource
    {
        public event Action<DeviceObservation>? Observed;
        public event Action<string>? Failed;
        public bool Healthy{get;private set;}
        public bool Disposed;
        public void Start()=>Healthy=true;
        public void Emit(DeviceObservation sample)=>Observed?.Invoke(sample);
        public void Fail(){Healthy=false;Failed?.Invoke("synthetic-stop");}
        public void Dispose(){Disposed=true;Healthy=false;}
    }
    private sealed class Session : IInteractiveSession
    {
        public bool IsInteractive=>true;public bool? IsLocked()=>true;
        public LockRequestResult RequestLock()=>throw new Exception("Observation-only source must not lock");
    }
    public static async Task RunAsync(Func<string,Func<Task>,Task> test)
    {
        void Check(bool value){if(!value)throw new Exception("Carried observation invariant");}
        await test("carried scanner forwards only the currently bound and authenticated GATT peer",()=>{
            string phone=new('f',64);var scanner=new Scanner();var peer=new BlePeerBinding(new('a',32),new("AABBCCDDEEFF","Random"),DateTimeOffset.UtcNow.AddSeconds(-1));
            VerifiedPhoneLink? proof=null;
            using var source=new CarriedPhoneObservationSource(phone,new(),new Session(),()=>peer,()=>proof,()=>true,scanner);
            source.Start();DeviceObservation Sample()=>new(peer.Target.Address,"",-50,DateTimeOffset.UtcNow,peer.Target.AddressType!,[]);
            scanner.Emit(Sample());Check(source.Tick().AcceptedSamples==0);
            proof=new(peer.ConnectionId,phone,Environment.TickCount64,true);scanner.Emit(Sample() with{Address="112233445566"});
            scanner.Emit(Sample());Check(source.Tick() is {AcceptedSamples:1,Authenticated:true,CanRequestConfirmation:false});return Task.CompletedTask;
        });
        await test("carried scanner failure and disposal cannot reactivate cached link evidence",()=>{
            string phone=new('f',64);var scanner=new Scanner();var peer=new BlePeerBinding(new('b',32),new("AABBCCDDEEFF","Public"),DateTimeOffset.UtcNow.AddSeconds(-1));
            var proof=new VerifiedPhoneLink(peer.ConnectionId,phone,Environment.TickCount64,true);
            var source=new CarriedPhoneObservationSource(phone,new(),new Session(),()=>peer,()=>proof,()=>true,scanner);
            source.Start();scanner.Emit(new(peer.Target.Address,"",-50,DateTimeOffset.UtcNow,"Public",[]));Check(source.Tick().AcceptedSamples==1);
            scanner.Fail();scanner.Start();proof=proof with{VerifiedAtMonotonicMilliseconds=Environment.TickCount64};
            Check(source.Tick() is {Authenticated:false,CanRequestConfirmation:false,Reason:"new-link-required"});
            source.Dispose();scanner.Emit(new(peer.Target.Address,"",-50,DateTimeOffset.UtcNow,"Public",[]));
            Check(scanner.Disposed && source.Tick() is {AcceptedSamples:1,Reason:"revoked"});return Task.CompletedTask;
        });
    }
}
