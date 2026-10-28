using System.Net;
using System.Threading.Channels;
using BandKey.Core;
using BandKey.Windows;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

// Fault-injection tests of production supervision; these are not physical BLE evidence.
internal static class BleRecoveryTests
{
    private static void Check(bool value){if(!value)throw new Exception("BLE recovery assertion");}
    private sealed class Connection(Func<TimeSpan,string?,CancellationToken,Task> run,Action dispose) : IBlePhoneConnection
    {
        public string Stage=>"forwarding";
        public BleTransportFailure? Failure=>null;
        public ushort Mtu=>23;
        public int Streams{get;init;}=1;
        public TimeSpan CompletedStreamSpan{get;init;}
        public long SentBytes=>3;
        public long ReceivedBytes=>5;
        public BlePeerBinding? Peer{get;init;}
        public Task RunAsync(IPAddress target,int port,TimeSpan duration,string? readyFile=null,string? stopFile=null,CancellationToken cancellation=default)=>run(duration,readyFile,cancellation);
        public ValueTask DisposeAsync(){dispose();return ValueTask.CompletedTask;}
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> test,string directory)
    {
        await test("BLE supervisor exposes only the live connection generation and clears it on stop",async()=>
        {
            var peer=new BlePeerBinding(new('a',32),new("AABBCCDDEEFF","Random"),DateTimeOffset.UtcNow);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int disposed=0;
            using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var transport=new BlePhoneTransport(()=>new Connection(async(_,_,token)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);},()=>disposed++){Peer=peer},(_,_)=>Task.CompletedTask);
            var running=transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(5),cancellation:cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Check(transport.Peer==peer);
            cancellation.Cancel();await running;Check(transport.Peer==null && disposed==1);
        });
        await test("BLE negotiates acknowledged indications while preserving notification-only phones",()=>
        {
            Check(BlePhoneTunnel.ChooseSubscription(GattCharacteristicProperties.Notify|GattCharacteristicProperties.Indicate)==GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            Check(BlePhoneTunnel.ChooseSubscription(GattCharacteristicProperties.Indicate)==GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            Check(BlePhoneTunnel.ChooseSubscription(GattCharacteristicProperties.Notify)==GattClientCharacteristicConfigurationDescriptorValue.Notify);
            bool refused=false;try{BlePhoneTunnel.ChooseSubscription(GattCharacteristicProperties.Read);}catch(IOException){refused=true;}Check(refused);
            return Task.CompletedTask;
        });
        await test("BLE health probe verifies the negotiated CCCD rather than assuming notifications",()=>
        {
            Check(BlePhoneTunnel.SubscriptionMatches(new byte[]{2,0},GattClientCharacteristicConfigurationDescriptorValue.Indicate));
            Check(BlePhoneTunnel.SubscriptionMatches(new byte[]{1,0},GattClientCharacteristicConfigurationDescriptorValue.Notify));
            foreach(byte[] bytes in new[]{new byte[]{0,0},new byte[]{1,0},new byte[]{3,0},new byte[]{2,1},new byte[]{2},new byte[]{2,0,0}})
                Check(!BlePhoneTunnel.SubscriptionMatches(bytes,GattClientCharacteristicConfigurationDescriptorValue.Indicate));
            Check(!BlePhoneTunnel.SubscriptionMatches(new byte[]{0,0},GattClientCharacteristicConfigurationDescriptorValue.None));return Task.CompletedTask;
        });
        await test("BLE closed incoming channel retains its fault and reaches automatic reconnection",async()=>
        {
            int created=0,disposed=0;using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var transport=new BlePhoneTransport(()=>
            {
                int id=++created;return new Connection(async(_,_,token)=>
                {
                    if(id==2){stop.Cancel();token.ThrowIfCancellationRequested();return;}
                    var frames=Channel.CreateBounded<byte[]>(1);var reading=BlePhoneTunnel.ReadFrameAsync(frames.Reader,token);
                    frames.Writer.TryComplete(new BleTransportException("gatt-idle-subscription-lost"));
                    await reading;
                },()=>disposed++);
            },(_,token)=>{token.ThrowIfCancellationRequested();return Task.CompletedTask;});
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(2),cancellation:stop.Token);
            Check(created==2 && disposed==2 && transport.Failures.Length==1 && transport.Failure?.Code=="gatt-idle-subscription-lost");
        });
        await test("BLE idle watchdog reports a lost remote subscription without an OS disconnect callback",async()=>
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try{await BleIdleWatchdog.RunAsync(()=>true,_=>throw new BleTransportException("gatt-idle-subscription-lost"),stop.Token,TimeSpan.FromMilliseconds(1));throw new Exception("Expected lost subscription");}
            catch(BleTransportException error){Check(error.Code=="gatt-idle-subscription-lost");}
        });
        await test("BLE idle watchdog bounds a stalled GATT read",async()=>
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try{await BleIdleWatchdog.RunAsync(()=>true,_=>blocked.Task,stop.Token,TimeSpan.FromMilliseconds(1),TimeSpan.FromMilliseconds(20));throw new Exception("Expected probe timeout");}
            catch(BleTransportException error){Check(error.Code=="gatt-health-timeout");}
            finally{blocked.TrySetResult();}
        });
        await test("BLE idle watchdog skips active traffic and treats user stop as cancellation",async()=>
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));int probes=0;
            try{await BleIdleWatchdog.RunAsync(()=>false,_=>{probes++;return Task.CompletedTask;},stop.Token,TimeSpan.FromMilliseconds(1));throw new Exception("Expected user stop");}
            catch(OperationCanceledException){Check(probes==0 && stop.IsCancellationRequested);}
        });
        await test("BLE reconnect disposes old connection and removes readiness before creating fresh state",async()=>
        {
            int created=0,disposed=0;using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ready=Path.Combine(directory,"recovery-ready");
            var transport=new BlePhoneTransport(()=>
            {
                int id=++created;Check(disposed==id-1 && !File.Exists(ready));
                return new Connection((_,path,token)=>
                {
                    File.WriteAllText(path!,"connected");
                    if(id==1)throw new BleTransportException("target-read",new IOException("hidden"));
                    stop.Cancel();return Task.FromCanceled(token);
                },()=>disposed++);
            },(_,token)=>{token.ThrowIfCancellationRequested();return Task.CompletedTask;});
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3),ready,cancellation:stop.Token);
            Check(created==2 && disposed==2 && !File.Exists(ready) && transport.Stage=="stopped");
            Check(transport.Failures.Length==1 && transport.Failure?.Code=="target-read" && transport.Streams==2 && transport.SentBytes==6 && transport.ReceivedBytes==10);
        });
        await test("BLE availability failures stop after four consecutive attempts and retain every failure",async()=>
        {
            int disposed=0;var transport=new BlePhoneTransport(()=>new Connection((_,_,_)=>throw new BleTransportException("target-connect-timeout"),()=>disposed++),(_,_)=>Task.CompletedTask);
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected retry exhaustion");}
            catch(BleTransportException){Check(transport.Attempts==4 && disposed==4 && transport.Failures.Length==4 && transport.Stage=="failed");}
        });
        await test("host fast retry disposes old state promptly and still stops on a permission fault",async()=>
        {
            int created=0,disposed=0;var durations=new List<TimeSpan>();var watch=System.Diagnostics.Stopwatch.StartNew();
            var transport=new BlePhoneTransport(()=>{
                int id=++created;Check(disposed==id-1);
                return new Connection((duration,_,_)=>{durations.Add(duration);throw new BleTransportException(id==1?"gatt-disconnected":"gatt-permission-denied");},()=>disposed++);
            },BlePhoneTransport.DelayHostRetry);
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Permission fault accepted");}
            catch(BleTransportException error){Check(error.Code=="gatt-permission-denied");}
            Check(created==2 && disposed==2 && durations[1]<durations[0] && watch.Elapsed<TimeSpan.FromSeconds(3));
            Check(BlePhoneTransport.HostRestartDelay(transport.Failure)==TimeSpan.FromSeconds(30));
            Check(BlePhoneTransport.HostRestartDelay(BleTransportFailure.From(new BleTransportException("connect-scan")))==TimeSpan.FromMilliseconds(500));
        });
        await test("BLE sustained completed traffic replenishes retries instead of exhausting a lifetime attempt counter",async()=>
        {
            int created=0,disposed=0;var waits=new List<int>();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var transport=new BlePhoneTransport(()=>{
                int id=++created;
                return new Connection((_,_,token)=>{
                    if(id==5){stop.Cancel();return Task.FromCanceled(token);}
                    throw new BleTransportException("target-read");
                },()=>disposed++){Streams=id==3?3:1,CompletedStreamSpan=id==3?TimeSpan.FromSeconds(60):TimeSpan.Zero};
            },(burst,_)=>{waits.Add(burst);return Task.CompletedTask;});
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3),cancellation:stop.Token);
            Check(created==5 && disposed==5 && waits.SequenceEqual(new[]{1,2,1,2}) && transport.FailureCount==4 && transport.ConsecutiveFailures==2 && transport.RecoveryBudgetResets==1);
        });
        await test("BLE idle or short completed traffic cannot replenish a failing connection burst",async()=>
        {
            foreach(var health in new[]{(streams:0,seconds:60),(streams:2,seconds:60),(streams:3,seconds:59)}){
                int created=0;var transport=new BlePhoneTransport(()=>{created++;return new Connection((_,_,_)=>throw new BleTransportException("stream-timeout"),()=>{}){Streams=health.streams,CompletedStreamSpan=TimeSpan.FromSeconds(health.seconds)};},(_,_)=>Task.CompletedTask);
                try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected burst exhaustion");}
                catch(BleTransportException){Check(created==4 && transport.ConsecutiveFailures==4 && transport.RecoveryBudgetResets==0);}
            }
        });
        await test("BLE bounded history retains total failures after repeated successful recoveries",async()=>
        {
            int created=0,disposed=0;using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var transport=new BlePhoneTransport(()=>{
                int id=++created;return new Connection((_,_,token)=>{
                    if(id==36){stop.Cancel();return Task.FromCanceled(token);}throw new BleTransportException("gatt-disconnected");
                },()=>disposed++){Streams=3,CompletedStreamSpan=TimeSpan.FromSeconds(60)};
            },(burst,_)=>{Check(burst==1);return Task.CompletedTask;});
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3),cancellation:stop.Token);
            Check(created==36 && disposed==36 && transport.FailureCount==35 && transport.Failures.Length==32 && transport.FailuresTruncated && transport.RecoveryBudgetResets==34 && transport.Failures[0].Attempt==4);
        });
        await test("BLE invalid frame is fatal and never automatically retried",async()=>
        {
            int disposed=0;var transport=new BlePhoneTransport(()=>new Connection((_,_,_)=>throw new BleTransportException("phone-frame-invalid"),()=>disposed++),(_,_)=>throw new Exception("Must not back off"));
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected protocol refusal");}
            catch(BleTransportException){Check(transport.Attempts==1 && disposed==1 && transport.Failures.Length==1 && transport.Failure?.Code=="phone-frame-invalid");}
        });
        await test("BLE healthy traffic cannot make an invalid frame or permission denial retryable",async()=>
        {
            foreach(string fatal in new[]{"phone-frame-invalid","gatt-permission-denied"}){
                int created=0,disposed=0;var waits=new List<int>();
                var transport=new BlePhoneTransport(()=>{int id=++created;return new Connection((_,_,_)=>throw new BleTransportException(id==1?"target-read":fatal),()=>disposed++){Streams=3,CompletedStreamSpan=TimeSpan.FromSeconds(60)};},(burst,_)=>{waits.Add(burst);return Task.CompletedTask;});
                try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected fatal refusal");}
                catch(BleTransportException){Check(created==2 && disposed==2 && waits.SequenceEqual(new[]{1}) && transport.Failure?.Code==fatal);}
            }
        });
        await test("BLE stop file cancels retry backoff without starting another connection",async()=>
        {
            var stopFile=Path.Combine(directory,"recovery-stop");int disposed=0;
            var transport=new BlePhoneTransport(()=>new Connection((_,_,_)=>throw new BleTransportException("gatt-disconnected"),()=>disposed++),async(_,token)=>
            {
                File.WriteAllText(stopFile,"stop");await Task.Delay(Timeout.Infinite,token);
            });
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3),stopFile:stopFile);
            Check(transport.Attempts==1 && disposed==1 && transport.Stage=="stopped" && transport.Failures.Length==1);
        });
        await test("BLE active cancellation stays clean and does not reconnect",async()=>
        {
            int disposed=0;using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var transport=new BlePhoneTransport(()=>new Connection((_,_,token)=>{stop.Cancel();return Task.FromCanceled(token);},()=>disposed++),(_,_)=>throw new Exception("Must not retry stop"));
            await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3),cancellation:stop.Token);
            Check(transport.Attempts==1 && disposed==1 && transport.Stage=="stopped" && transport.Failure==null);
        });
        await test("BLE retries share the original lifetime instead of extending it",async()=>
        {
            var durations=new List<TimeSpan>();int created=0,disposed=0;
            var transport=new BlePhoneTransport(()=>
            {
                int id=++created;return new Connection(async(duration,_,token)=>
                {
                    durations.Add(duration);
                    if(id==1)throw new BleTransportException("target-connect-timeout");
                    await Task.Delay(Timeout.Infinite,token);
                },()=>disposed++);
            },(_,token)=>Task.Delay(20,token));
            var duration=TimeSpan.FromMilliseconds(250);
            await transport.RunAsync(IPAddress.Loopback,19443,duration);
            Check(created==2 && disposed==2 && durations.All(value=>value<=duration) && durations[1]<durations[0] && transport.Stage=="stopped");
        });
        await test("BLE diagnostic keeps nested GATT reason and excludes exception messages",()=>
        {
            var failure=BleTransportFailure.From(new BleTransportException("phone-write",new BleTransportException("gatt-write-unreachable",new IOException("secret peer text"))));
            Check(failure.Code=="gatt-write-unreachable" && !System.Text.Json.JsonSerializer.Serialize(failure).Contains("secret peer text"));return Task.CompletedTask;
        });
    }
}
