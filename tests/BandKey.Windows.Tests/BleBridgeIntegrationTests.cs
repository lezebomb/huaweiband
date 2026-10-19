using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using BandKey.Core;
using BandKey.Windows;

// GATT is simulated; sockets, TLS, production framing, stream forwarding and signatures are real.
internal static class BleBridgeIntegrationTests
{
    private static void Check(bool value){if(!value)throw new Exception("BLE integration assertion");}
    private sealed class Wire
    {
        private static Channel<byte[]> Queue()=>Channel.CreateBounded<byte[]>(128);
        private readonly Channel<byte[]> toPc=Queue(),toPhone=Queue();
        internal Task PcSend(BlePipeFrame value,CancellationToken token)=>toPhone.Writer.WriteAsync(value.Encode(),token).AsTask();
        internal Task PhoneSend(BlePipeFrame value,CancellationToken token)=>toPc.Writer.WriteAsync(value.Encode(),token).AsTask();
        internal async Task<BlePipeFrame> PcRead(CancellationToken token)=>BlePipeFrame.Decode(await toPc.Reader.ReadAsync(token));
        internal async Task<BlePipeFrame> PhoneRead(CancellationToken token)=>BlePipeFrame.Decode(await toPhone.Reader.ReadAsync(token));
    }
    private sealed class Proxy : IAsyncDisposable
    {
        private readonly Wire wire=new();private readonly TcpListener listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource stop=new(TimeSpan.FromSeconds(20));
        private readonly Task phone,pc;internal long Up,Down;internal int Streams;
        internal int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
        internal Proxy(int target,int payload=7)
        {
            listener.Start(1);phone=PhoneLoop(payload);pc=PcLoop(target,payload);
        }
        private async Task PcLoop(int port,int payload)
        {
            try{while(!stop.IsCancellationRequested){var open=await wire.PcRead(stop.Token);var stats=await BleStreamForwarder.RunAsync(open,IPAddress.Loopback,port,wire.PcRead,wire.PcSend,()=>payload,stop.Token);Up+=stats.ReceivedBytes;Down+=stats.SentBytes;Streams++;}}
            catch(Exception error)when(error is OperationCanceledException or IOException or SocketException){stop.Cancel();}
        }
        private async Task PhoneLoop(int payload)
        {
            ulong session=0;
            try{while(!stop.IsCancellationRequested){using var client=await listener.AcceptTcpClientAsync(stop.Token);client.NoDelay=true;var stream=client.GetStream();ulong id=++session;uint seq=0;
                await wire.PhoneSend(new(BlePipeFrame.Open,id,seq++,[]),stop.Token);var receive=new BlePipeReceiver(id,BlePipeFrame.Ready);receive.Accept(await wire.PhoneRead(stop.Token));
                async Task Upload(){try{while(true){byte[] bytes=new byte[payload];int count=await stream.ReadAsync(bytes,stop.Token);await wire.PhoneSend(new(count==0?BlePipeFrame.Eof:BlePipeFrame.Data,id,seq++,bytes[..count]),stop.Token);if(count==0)return;}}catch{stop.Cancel();throw;}}
                async Task Download(){try{while(true){var frame=await wire.PhoneRead(stop.Token);receive.Accept(frame);if(frame.Kind==BlePipeFrame.Eof){client.Client.Shutdown(SocketShutdown.Send);return;}if(frame.Kind==BlePipeFrame.Abort)throw new IOException("Abort");await stream.WriteAsync(frame.Payload,stop.Token);}}catch{stop.Cancel();throw;}}
                await Task.WhenAll(Upload(),Download());
            }}catch(Exception error)when(error is OperationCanceledException or IOException or SocketException){stop.Cancel();}
        }
        public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();try{await Task.WhenAll(phone,pc).WaitAsync(TimeSpan.FromSeconds(3));}finally{stop.Dispose();}}
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> test,string directory)
    {
        await test("BLE simulated MTU23 pipe carries 60KB both ways and half-close",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{
                var wire=new Wire();await wire.PhoneSend(new(1,9,0,[]),budget.Token);
                var forwarding=BleStreamForwarder.RunAsync(await wire.PcRead(budget.Token),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,wire.PcSend,()=>7,budget.Token);
                using var server=await target.AcceptTcpClientAsync(budget.Token);var network=server.GetStream();var receive=new BlePipeReceiver(9,2);receive.Accept(await wire.PhoneRead(budget.Token));
                byte[] up=RandomNumberGenerator.GetBytes(60000),down=RandomNumberGenerator.GetBytes(60000);
                async Task Upload(){uint seq=1;for(int offset=0;offset<up.Length;offset+=7)await wire.PhoneSend(new(3,9,seq++,up[offset..Math.Min(offset+7,up.Length)]),budget.Token);await wire.PhoneSend(new(4,9,seq,[]),budget.Token);}
                async Task Server(){byte[] captured=new byte[up.Length];await network.ReadExactlyAsync(captured,budget.Token);Check(captured.SequenceEqual(up));Check(await network.ReadAsync(new byte[1],budget.Token)==0);await network.WriteAsync(down,budget.Token);server.Client.Shutdown(SocketShutdown.Send);}
                async Task Download(){using var output=new MemoryStream();while(true){var frame=await wire.PhoneRead(budget.Token);receive.Accept(frame);if(frame.Kind==4)break;Check(frame.Payload.Length<=7);output.Write(frame.Payload);}Check(output.ToArray().SequenceEqual(down));}
                await Task.WhenAll(Upload(),Server(),Download());var stats=await forwarding;Check(stats.SentBytes==60000 && stats.ReceivedBytes==60000);
            }finally{target.Stop();}
        });
        await test("BLE target closes first and delayed phone EOF completes without another target shutdown",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var wire=new Wire();var forwarding=BleStreamForwarder.RunAsync(new(1,22,0,[]),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,wire.PcSend,()=>160,budget.Token);
                using var server=await target.AcceptTcpClientAsync(budget.Token);await wire.PhoneRead(budget.Token);
                await server.GetStream().WriteAsync(new byte[]{4,5,6},budget.Token);server.Client.Shutdown(SocketShutdown.Send);
                var data=await wire.PhoneRead(budget.Token);var eof=await wire.PhoneRead(budget.Token);Check(data.Payload.SequenceEqual(new byte[]{4,5,6}) && eof.Kind==BlePipeFrame.Eof);
                server.Client.Dispose();
                await wire.PhoneSend(new(BlePipeFrame.Eof,22,1,[]),budget.Token);
                var stats=await forwarding;Check(stats.SentBytes==3 && stats.ReceivedBytes==0);
            }
            finally{target.Stop();}
        });
        foreach(string fault in new[]{"sequence","session","abort","overflow","idle","cancel"})
        await test("BLE production forwarder stops on simulated "+fault,async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{
                var wire=new Wire();var forwarding=BleStreamForwarder.RunAsync(new(1,9,0,[]),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,wire.PcSend,()=>160,budget.Token,fault=="idle"?TimeSpan.FromMilliseconds(100):null);
                using var server=await target.AcceptTcpClientAsync(budget.Token);await wire.PhoneRead(budget.Token);
                if(fault=="cancel")budget.Cancel();
                else if(fault=="sequence")await wire.PhoneSend(new(3,9,2,[1]),budget.Token);
                else if(fault=="session")await wire.PhoneSend(new(3,10,1,[1]),budget.Token);
                else if(fault=="abort")await wire.PhoneSend(new(5,9,1,[]),budget.Token);
                else if(fault=="overflow"){
                    _=Task.Run(async()=>{try{await server.GetStream().CopyToAsync(Stream.Null,budget.Token);}catch(OperationCanceledException){}catch(IOException){}},budget.Token);
                    for(uint seq=1;seq<=820;seq++)await wire.PhoneSend(new(3,9,seq,new byte[160]),budget.Token);
                }
                bool refused=false;try{await forwarding.WaitAsync(TimeSpan.FromSeconds(3));}
                catch(Exception error)when(error is IOException or OperationCanceledException)
                {
                    refused=true;
                    if(fault=="cancel")Check(error is OperationCanceledException);
                    else Check(error is BleTransportException failure && failure.Code==(fault=="idle"?"stream-timeout":fault=="abort"?"phone-aborted":"phone-frame-invalid"));
                }
                Check(refused);
            }finally{target.Stop();}
        });
        await test("BLE diagnostics preserve download failure before cancelling upload and redact exception text",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var wire=new Wire();
                Task Write(BlePipeFrame frame,CancellationToken token)=>frame.Kind==BlePipeFrame.Data?Task.FromException(new IOException("untrusted-private-peer-message")):wire.PcSend(frame,token);
                var forwarding=BleStreamForwarder.RunAsync(new(1,19,0,[]),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,Write,()=>160,budget.Token);
                using var server=await target.AcceptTcpClientAsync(budget.Token);await wire.PhoneRead(budget.Token);
                await server.GetStream().WriteAsync(new byte[]{1,2,3},budget.Token);
                try{await forwarding;throw new Exception("Expected download failure");}
                catch(BleTransportException error)
                {
                    var failure=BleTransportFailure.From(error);
                    Check(failure.Code=="phone-write" && failure.ExceptionType==nameof(IOException));
                    Check(!System.Text.Json.JsonSerializer.Serialize(failure).Contains("untrusted-private-peer-message"));
                }
            }
            finally{target.Stop();}
        });
        await test("BLE target TCP reset reports numeric socket cause instead of sibling cancellation",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var wire=new Wire();var forwarding=BleStreamForwarder.RunAsync(new(1,20,0,[]),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,wire.PcSend,()=>160,budget.Token);
                using var server=await target.AcceptTcpClientAsync(budget.Token);await wire.PhoneRead(budget.Token);
                server.Client.LingerState=new(true,0);server.Client.Dispose();
                try{await forwarding;throw new Exception("Expected TCP reset");}
                catch(BleTransportException error)
                {
                    var failure=BleTransportFailure.From(error);
                    Check(failure.Code=="target-read" && failure.SocketError==(int)SocketError.ConnectionReset);
                }
            }
            finally{target.Stop();}
        });
        await test("BLE READY write failure has a distinct safe diagnostic",async()=>
        {
            var target=new TcpListener(IPAddress.Loopback,0);target.Start();using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var wire=new Wire();
                var forwarding=BleStreamForwarder.RunAsync(new(1,21,0,[]),IPAddress.Loopback,((IPEndPoint)target.LocalEndpoint).Port,wire.PcRead,(_,_)=>Task.FromException(new IOException("not for logs")),()=>160,budget.Token);
                using var server=await target.AcceptTcpClientAsync(budget.Token);
                try{await forwarding;throw new Exception("Expected READY failure");}
                catch(BleTransportException error){Check(BleTransportFailure.From(error).Code=="ready-write");}
            }
            finally{target.Stop();}
        });
        foreach(string mode in new[]{"approve-replay","deny","wrong-pin","invalid-signature","revoked"})
        await test("BLE simulated TLS native authorization: "+mode,async()=>
        {
            await using var f=new WindowsGrantRelayIntegrationTests.Fixture(directory);await f.Start();await using var proxy=new Proxy(f.Client.BaseAddress!.Port);
            using var client=new HttpClient(new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,cert,_,_)=>mode!="wrong-pin" && cert!=null && BridgeServer.CertificatePin(cert)==BridgeServer.CertificatePin(f.Certificate)}){BaseAddress=new Uri($"https://127.0.0.1:{proxy.Port}"),Timeout=TimeSpan.FromSeconds(10)};client.DefaultRequestHeaders.ConnectionClose=true;
            if(mode=="wrong-pin"){bool refused=false;try{using var bad=await client.PostAsJsonAsync("/v2/windows/poll",f.PollRequest());}catch(HttpRequestException){refused=true;}Check(refused && !File.Exists(f.ApprovalPath));return;}
            using var poll=await client.PostAsJsonAsync("/v2/windows/poll",f.PollRequest());poll.EnsureSuccessStatusCode();var prompt=(await poll.Content.ReadFromJsonAsync<WindowsGrantPrompt>())!;
            Check(Protocol.Verify(Protocol.ExportPublicKey(f.Pc),WindowsGrantRelayWire.PromptText(prompt),prompt.Signature));
            var decision=f.Decision(prompt,mode!="deny");if(mode=="invalid-signature")decision=decision with{Signature=Convert.ToBase64String(new byte[64])};if(mode=="revoked")f.Current=false;
            using var response=await client.PostAsJsonAsync("/v2/windows/decision",decision);var result=(await response.Content.ReadFromJsonAsync<WindowsGrantRelayResult>())!;Check(!result.WindowsUnlocked && result.TestOnly);
            if(mode=="approve-replay"){
                Check(result.Authorized && result.Submitted && File.ReadAllBytes(f.ApprovalPath).Length==500);
                Check(WindowsGrantWire.Verify(WindowsGrantWire.ReadChallenge(f.Body),File.ReadAllBytes(f.ApprovalPath),WindowsGrantWire.ExportNativePublicKey(f.Phone),WindowsGrantRelayWire.FileTimeMilliseconds(f.Wall)));
                using var replay=await client.PostAsJsonAsync("/v2/windows/decision",decision);Check(replay.StatusCode==HttpStatusCode.Forbidden);
            }else Check(!result.Submitted && !result.Authorized && !File.Exists(f.ApprovalPath));
            Check(proxy.Up>0 && proxy.Down>0);
        });
    }
}
