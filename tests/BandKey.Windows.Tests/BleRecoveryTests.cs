using System.Net;
using BandKey.Windows;

// Fault-injection tests of production supervision; these are not physical BLE evidence.
internal static class BleRecoveryTests
{
    private static void Check(bool value){if(!value)throw new Exception("BLE recovery assertion");}
    private sealed class Connection(Func<TimeSpan,string?,CancellationToken,Task> run,Action dispose) : IBlePhoneConnection
    {
        public string Stage=>"forwarding";
        public BleTransportFailure? Failure=>null;
        public ushort Mtu=>23;
        public int Streams=>1;
        public long SentBytes=>3;
        public long ReceivedBytes=>5;
        public Task RunAsync(IPAddress target,int port,TimeSpan duration,string? readyFile=null,string? stopFile=null,CancellationToken cancellation=default)=>run(duration,readyFile,cancellation);
        public ValueTask DisposeAsync(){dispose();return ValueTask.CompletedTask;}
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> test,string directory)
    {
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
        await test("BLE availability failures stop after four attempts and retain every failure",async()=>
        {
            int disposed=0;var transport=new BlePhoneTransport(()=>new Connection((_,_,_)=>throw new BleTransportException("target-connect-timeout"),()=>disposed++),(_,_)=>Task.CompletedTask);
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected retry exhaustion");}
            catch(BleTransportException){Check(transport.Attempts==4 && disposed==4 && transport.Failures.Length==4 && transport.Stage=="failed");}
        });
        await test("BLE invalid frame is fatal and never automatically retried",async()=>
        {
            int disposed=0;var transport=new BlePhoneTransport(()=>new Connection((_,_,_)=>throw new BleTransportException("phone-frame-invalid"),()=>disposed++),(_,_)=>throw new Exception("Must not back off"));
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromSeconds(3));throw new Exception("Expected protocol refusal");}
            catch(BleTransportException){Check(transport.Attempts==1 && disposed==1 && transport.Failures.Length==1 && transport.Failure?.Code=="phone-frame-invalid");}
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
