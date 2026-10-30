using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BandKey.Core;
using BandKey.Windows;

internal static class WindowsGrantRelayIntegrationTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private static void Check(bool value) { if (!value) throw new Exception("VM relay assertion failed"); }
    internal sealed class Session : IInteractiveSession
    {
        public bool IsInteractive=>true;public bool? Locked=true;public int Locks;
        public bool? IsLocked()=>Locked;
        public LockRequestResult RequestLock(){Locks++;return new(true,"submitted");}
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly ECDsa Pc = ECDsa.Create(ECCurve.NamedCurves.nistP256), Phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly BridgeServer Server = new();
        internal readonly string Root;
        internal readonly PublicDevice Device;
        internal readonly WindowsGrantDirectoryExchange Exchange;
        internal readonly WindowsGrantRelay Relay;
        internal readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate;
        internal readonly HttpClient Client;
        internal readonly Session Desktop=new();
        internal readonly FixedPhoneChannel? Fixed;
        internal bool Current = true;
        internal long Wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mono = 100;
        internal byte[] Body = [];
        internal string RequestPath = "";
        internal string ApprovalPath => RequestPath + ".approval";
        internal Fixture(string directory,bool fixedPhone=false,bool freshLock=false,bool phoneReturn=false)
        {
            Root = Path.Combine(directory, "native-exchange-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "Exchange"));
            var publicKey = Protocol.ExportPublicKey(Phone); Device = new(Protocol.Fingerprint(publicKey), "VM integration test", publicKey, ConfirmationSources.TestClient, true);
            Exchange = new(Root, Sid, 5, () => Current, () => WindowsGrantRelayWire.FileTimeMilliseconds(Wall));
            if(fixedPhone) Fixed=new(Pc,Device,Exchange,Desktop,()=>Current,false,()=>Wall,()=>Mono,useFreshLockRequest:freshLock,renewOnAuthenticatedReturn:phoneReturn);
            Relay = new(Pc, Device, Exchange, () => Current, () => Wall, () => Mono, proximityCurrent:()=>Fixed?.CanSubmit??true);
            Certificate = new Store(Path.Combine(Root, "pc-identity")).LoadTlsCertificate();
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && BridgeServer.CertificatePin(c) == BridgeServer.CertificatePin(Certificate) })
                { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            Port = port; NewRequest();
        }
        private int Port { get; }
        internal Task Start() => Server.StartAsync(new AppSettings { Port = Port }, Certificate, new AuthorizationBroker(Relay.PcId, Pc), _ => { }, windowsRelay: Relay,fixedPhone:Fixed);
        internal void NewRequest(string sid = Sid, uint session = 5, ulong lifetime = 30000)
        {
            ulong now = WindowsGrantRelayWire.FileTimeMilliseconds(Wall);
            Body = WindowsGrantWire.WriteChallenge(new(session, now, now + lifetime, sid, Protocol.Nonce()));
            RequestPath = Path.Combine(Root, "Exchange", Convert.ToHexStringLower(Body.AsSpan(36, 32)) + ".request"); File.WriteAllBytes(RequestPath, Body);
        }
        private FixedPhoneContext? fixedContext;private long fixedSequence;
        internal BandConnectionReport StateProof(bool? connected,PollRequest? inlinePoll=null){
            if(fixedContext==null){var p=PollRequest();fixedContext=Fixed!.GetContext(p with{Signature=Protocol.Sign(Phone,FixedPhoneWire.ContextPollText(p))});}
            var report=new BandConnectionReport(Relay.PcId,Device.DeviceId,fixedContext.ContextNonce,++fixedSequence,Wall,new string('a',64),connected,"",inlinePoll);
            return report with{Signature=Protocol.Sign(Phone,FixedPhoneWire.ReportText(report))};
        }
        internal async Task<FixedPhoneResult> FixedState(bool? connected,PollRequest? inlinePoll=null){
            using var response=await Client.PostAsJsonAsync("/v2/windows/fixed/state",StateProof(connected,inlinePoll));response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<FixedPhoneResult>())!;
        }
        internal async Task<RequestRenewalPrompt> NearForRenewal(){
            await FixedState(true);Wall+=4000;Mono+=4000;
            return (await FixedState(true)).Renewal??throw new Exception("Expected renewal");
        }
        internal RequestRenewalDecision RenewalProof(RequestRenewalPrompt prompt,PollRequest? inlinePoll=null){
            var proof=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(Convert.FromBase64String(prompt.Body)),Phone);
            return new(prompt.PcId,prompt.DeviceId,prompt.ContextNonce,prompt.Body,Convert.ToBase64String(proof.AsSpan(436)),inlinePoll);
        }
        internal async Task ReadyFixedNative(){
            var renewal=await NearForRenewal();Check(Fixed!.Renew(RenewalProof(renewal)).Accepted);
            // Model the SYSTEM provider consuming the NEW file before it creates WIN.
            File.Delete(Path.Combine(Root,"Exchange","request-renewal.bin"));
            Wall++;Mono++;NewRequest();Check(Fixed.CanSubmit);
        }
        internal PollRequest PollRequest()
        {
            var p = new PollRequest(Relay.PcId, Device.DeviceId, Protocol.Nonce(), Wall / 1000, "");
            return p with { Signature = Protocol.Sign(Phone, WindowsGrantRelayWire.PollText(p)) };
        }
        internal async Task<WindowsGrantPrompt> Poll()
        {
            using var response = await Client.PostAsJsonAsync("/v2/windows/poll", PollRequest()); response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<WindowsGrantPrompt>())!;
        }
        internal WindowsGrantDecision Decision(WindowsGrantPrompt prompt, bool approved = true)
        {
            var d = new WindowsGrantDecision(Relay.PcId, Device.DeviceId, prompt.Challenge, approved, "");
            return d with { Signature = approved ? Convert.ToBase64String(Phone.SignData(Convert.FromBase64String(prompt.Challenge), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) : Protocol.Sign(Phone, WindowsGrantRelayWire.DenialText(d)) };
        }
        internal Task<HttpResponseMessage> Decide(WindowsGrantDecision decision) => Client.PostAsJsonAsync("/v2/windows/decision", decision);
        public async ValueTask DisposeAsync() { Relay.Revoke(); await Server.DisposeAsync(); Client.Dispose(); Certificate.Dispose(); Pc.Dispose(); Phone.Dispose(); }
    }
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string directory)
    {
        await test("renewal file retry is idempotent and preserves a different live proof",async()=>{
            await using var f=new Fixture(directory);var first=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal()),f.Phone);
            Check(f.Exchange.SubmitRequestRenewal(first) && f.Exchange.SubmitRequestRenewal(first));f.Wall++;f.Mono++;
            var other=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal()),f.Phone);
            Check(!f.Exchange.SubmitRequestRenewal(other) && File.ReadAllBytes(Path.Combine(f.Root,"Exchange","request-renewal.bin")).SequenceEqual(first) && !File.Exists(Path.Combine(f.Root,"Exchange","request-renewal.tmp")));
        });
        await test("renewal file atomically replaces only an expired bound refresh",async()=>{
            await using var f=new Fixture(directory);var old=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal()),f.Phone);Check(f.Exchange.SubmitRequestRenewal(old));
            f.Wall+=11000;f.Mono+=11000;var next=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal()),f.Phone);
            Check(f.Exchange.SubmitRequestRenewal(next) && !f.Exchange.SubmitRequestRenewal(old) && File.ReadAllBytes(Path.Combine(f.Root,"Exchange","request-renewal.bin")).SequenceEqual(next) && !File.Exists(f.ApprovalPath));
        });
        await test("renewal recovery preserves malformed and foreign account or session files",async()=>{
            for(int variant=0;variant<3;variant++){
                await using var f=new Fixture(directory);var old=WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal());
                byte[] sentinel=variant==0?[1,2,3]:WindowsRequestRenewalWire.Sign(old with{UserSid=variant==1?"S-1-5-21-111-222-333-1002":Sid,WindowsSessionId=variant==2?6u:5u},f.Phone);
                var path=Path.Combine(f.Root,"Exchange","request-renewal.bin");File.WriteAllBytes(path,sentinel);f.Wall+=11000;f.Mono+=11000;
                var next=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(f.Exchange.CreateRequestRenewal()),f.Phone);
                try{Check(!f.Exchange.SubmitRequestRenewal(next));}catch(IOException){}catch(FormatException){}
                Check(File.ReadAllBytes(path).SequenceEqual(sentinel) && !File.Exists(Path.Combine(f.Root,"Exchange","request-renewal.tmp")));
            }
        });
        await test("two-minute HTTPS return recovers missed native refresh and rejects old confirmation",async()=>{
            await using var f=new Fixture(directory,true,true,true);await f.Start();f.Desktop.Locked=false;await f.FixedState(true);f.Wall+=4000;f.Mono+=4000;await f.FixedState(true);
            f.Desktop.Locked=true;f.Wall++;f.Mono++;f.NewRequest(lifetime:120000);var original=(await f.FixedState(true,f.PollRequest())).Prompt??throw new Exception("Initial lock prompt missing");var oldDecision=f.Decision(original);
            f.Wall+=130000;f.Mono+=130000;Check((await f.FixedState(true)).Renewal==null);f.Wall+=1000;f.Mono+=1000;
            var lost=(await f.FixedState(true)).Renewal??throw new Exception("Return refresh missing");
            using(var first=await f.Client.PostAsJsonAsync("/v2/windows/fixed/renew",f.RenewalProof(lost))){first.EnsureSuccessStatusCode();}
            // SYSTEM has not consumed this file before its 10s deadline.
            RequestRenewalPrompt? recovered=null;for(int i=0;i<4;i++){f.Wall+=3000;f.Mono+=3000;recovered=(await f.FixedState(true)).Renewal;}
            Check(recovered!=null && recovered.Body!=lost.Body && !File.Exists(f.ApprovalPath));
            using(var retry=await f.Client.PostAsJsonAsync("/v2/windows/fixed/renew",f.RenewalProof(recovered!))){retry.EnsureSuccessStatusCode();}
            using(var stale=await f.Client.PostAsJsonAsync("/v2/windows/fixed/renew",f.RenewalProof(lost))){Check(stale.StatusCode==HttpStatusCode.Forbidden);}
            File.Delete(Path.Combine(f.Root,"Exchange","request-renewal.bin"));f.Wall++;f.Mono++;f.NewRequest(lifetime:120000);
            var current=(await f.FixedState(true,f.PollRequest())).Prompt??throw new Exception("Recovered prompt missing");Check(current.Challenge!=original.Challenge);
            using var denied=await f.Decide(oldDecision);Check(denied.StatusCode==HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath) && f.Desktop.Locks==0);
        });
        await test("authenticated phone channel return renews an expired request while refusing the old signed approval",async()=>{
            await using var f=new Fixture(directory,true,phoneReturn:true);await f.Start();await f.ReadyFixedNative();var old=await f.Poll();var oldDecision=f.Decision(old);
            f.Wall+=60000;f.Mono+=60000;Check((await f.FixedState(true)).Renewal==null);f.Wall+=4000;f.Mono+=4000;
            var returned=await f.FixedState(true);Check(returned.Renewal!=null && f.Fixed!.Snapshot.PhoneChannelReturns==1 && f.Desktop.Locks==0);
            Check(f.Fixed!.Renew(f.RenewalProof(returned.Renewal!)).Accepted);File.Delete(Path.Combine(f.Root,"Exchange","request-renewal.bin"));f.Wall++;f.Mono++;f.NewRequest();
            var current=await f.Poll();Check(current.Challenge!=old.Challenge);using var refused=await f.Decide(oldDecision);Check(refused.StatusCode==HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath));
            using var approved=await f.Decide(f.Decision(current));var status=await approved.Content.ReadFromJsonAsync<WindowsGrantRelayResult>();Check(approved.IsSuccessStatusCode && status is{Submitted:true,WindowsUnlocked:false} && File.Exists(f.ApprovalPath));
        });
        async Task Case(string name, Func<Fixture, Task> action) => await test(name, async () => { await using var f = new Fixture(directory); await f.Start(); await action(f); });
        await test("fresh lock HTTPS returns the current signed challenge without renewal or automatic approval",async()=>{
            await using var f=new Fixture(directory,true,true);f.Desktop.Locked=false;await f.Start();
            await f.FixedState(true);f.Wall+=4000;f.Mono+=4000;await f.FixedState(true);
            f.Desktop.Locked=true;f.Wall++;f.Mono++;f.NewRequest();
            var result=await f.FixedState(true,f.PollRequest());
            Check(result.Renewal==null && result.Prompt!=null && result.CanPoll && f.Fixed!.Snapshot.DirectRequest && f.Fixed.Snapshot.Renewals==0 && !File.Exists(f.ApprovalPath));
            using var approval=await f.Decide(f.Decision(result.Prompt!));var status=await approval.Content.ReadFromJsonAsync<WindowsGrantRelayResult>();
            Check(approval.IsSuccessStatusCode && status is{Submitted:true,WindowsUnlocked:false} && File.Exists(f.ApprovalPath));
        });
        await test("replaced native HTTPS request rejects the old decision and recovers through a fresh signed renewal",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();await f.ReadyFixedNative();var old=await f.Poll();
            f.Wall++;f.Mono++;f.NewRequest();Check(!f.Fixed!.CanSubmit);
            var state=await f.FixedState(true,f.PollRequest());Check(state.Renewal!=null && state.Prompt==null);
            using var refused=await f.Decide(f.Decision(old));Check(refused.StatusCode==HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath));
            var proof=f.RenewalProof(state.Renewal!);Check(f.Fixed.Renew(proof).Accepted);f.Wall++;f.Mono++;f.NewRequest();
            var prompt=await f.Poll();Check(prompt.Challenge!=old.Challenge && f.Fixed.Snapshot.ReplacementRecoveries==1);
            using var accepted=await f.Decide(f.Decision(prompt));Check(accepted.IsSuccessStatusCode && File.Exists(f.ApprovalPath));
        });
        await Case("VM relay HTTPS signs native request with PC identity", async f =>
        { var p = await f.Poll(); Check(p.Challenge == Convert.ToBase64String(f.Body) && Protocol.Verify(Protocol.ExportPublicKey(f.Pc), WindowsGrantRelayWire.PromptText(p), p.Signature)); });
        await Case("VM relay HTTPS approval writes exactly 500 bytes, native signature valid, no unlock claim", async f =>
        {
            var p = await f.Poll(); using var response = await f.Decide(f.Decision(p)); var result = await response.Content.ReadFromJsonAsync<WindowsGrantRelayResult>();
            Check(response.IsSuccessStatusCode && result is { Authorized: true, Submitted: true, WindowsUnlocked: false, TestOnly: true });
            Check(WindowsGrantWire.Verify(WindowsGrantWire.ReadChallenge(f.Body), File.ReadAllBytes(f.ApprovalPath), WindowsGrantWire.ExportNativePublicKey(f.Phone), WindowsGrantRelayWire.FileTimeMilliseconds(f.Wall)));
        });
        await Case("VM relay HTTPS repeated approval cannot write a second proof", async f =>
        {
            var d = f.Decision(await f.Poll()); using var first = await f.Decide(d); Check(first.IsSuccessStatusCode); byte[] saved = File.ReadAllBytes(f.ApprovalPath);
            using var second = await f.Decide(d); Check(second.StatusCode == HttpStatusCode.Forbidden && saved.SequenceEqual(File.ReadAllBytes(f.ApprovalPath)));
        });
        await Case("VM relay refuses invalid P1363 signature without creating approval", async f =>
        { var d = f.Decision(await f.Poll()) with { Signature = Convert.ToBase64String(new byte[64]) }; using var r = await f.Decide(d); Check(r.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath)); });
        await Case("VM relay signed denial consumes request without writing approval", async f =>
        {
            var p = await f.Poll(); using var r = await f.Decide(f.Decision(p, false)); var result = await r.Content.ReadFromJsonAsync<WindowsGrantRelayResult>();
            Check(r.IsSuccessStatusCode && result is { Authorized: false, Submitted: false, Code: "user-denied" });
            using var replay = await f.Decide(f.Decision(p)); Check(replay.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath));
        });
        await Case("VM relay unsigned poll and replay rejected by HTTPS API", async f =>
        {
            var p = f.PollRequest(); using var forged = await f.Client.PostAsJsonAsync("/v2/windows/poll", p with { Signature = "AAAA" }); Check(forged.StatusCode == HttpStatusCode.Unauthorized);
            using var first = await f.Client.PostAsJsonAsync("/v2/windows/poll", p); Check(first.IsSuccessStatusCode);
            using var replay = await f.Client.PostAsJsonAsync("/v2/windows/poll", p); Check(replay.StatusCode == HttpStatusCode.Unauthorized);
        });
        await Case("VM relay native request substitution rejected", async f =>
        { var p = await f.Poll(); File.Delete(f.RequestPath); f.Wall++; f.NewRequest(); using var r = await f.Decide(f.Decision(p)); Check(r.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath)); });
        await Case("VM relay expiry rejects phone decision", async f =>
        { var p = await f.Poll(); f.Wall += 30000; f.Mono += 30000; using var r = await f.Decide(f.Decision(p)); Check(r.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath)); });
        await Case("two minute HTTPS native exchange accepts confirmation after ninety seconds", async f =>
        {
            File.Delete(f.RequestPath); f.NewRequest(lifetime:120000); var p = await f.Poll(); f.Wall += 90000; f.Mono += 90000;
            using var r = await f.Decide(f.Decision(p)); Check(r.IsSuccessStatusCode && File.Exists(f.ApprovalPath));
            using var replay = await f.Decide(f.Decision(p)); Check(replay.StatusCode == HttpStatusCode.Forbidden);
        });
        await Case("two minute HTTPS native exchange refuses exact deadline", async f =>
        {
            File.Delete(f.RequestPath); f.NewRequest(lifetime:120000); var p = await f.Poll(); f.Wall += 120000; f.Mono += 120000;
            using var r = await f.Decide(f.Decision(p)); Check(r.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath));
        });
        await Case("VM relay revocation rejects previously issued prompt", async f =>
        { var p = await f.Poll(); f.Relay.Revoke(); using var r = await f.Decide(f.Decision(p)); Check(r.StatusCode == HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath)); });
        await Case("VM relay changed native policy rejects and remains revoked", async f =>
        {
            var p = await f.Poll(); f.Current = false; using var r = await f.Decide(f.Decision(p)); Check(r.StatusCode == HttpStatusCode.Forbidden); f.Current = true;
            using var next = await f.Client.PostAsJsonAsync("/v2/windows/poll", f.PollRequest()); Check(next.StatusCode == HttpStatusCode.Unauthorized && !File.Exists(f.ApprovalPath));
        });
        await Case("VM relay malformed JSON is bounded client failure", async f =>
        { using var r = await f.Client.PostAsync("/v2/windows/decision", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")); Check((int)r.StatusCode is >= 400 and < 500); });
        await Case("VM relay TLS pin mismatch prevents transport", async f =>
        {
            using var wrong = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => false });
            try { await wrong.PostAsJsonAsync(f.Client.BaseAddress + "v2/windows/poll", f.PollRequest()); } catch (HttpRequestException) { return; }
            throw new Exception("Wrong pin accepted");
        });
        await Case("native exchange rejects wrong SID and session", async f =>
        { File.Delete(f.RequestPath); f.NewRequest("S-1-5-21-111-222-333-1002"); Check(f.Exchange.ReadCurrentChallenge() == null); File.Delete(f.RequestPath); f.NewRequest(session: 6); Check(f.Exchange.ReadCurrentChallenge() == null); await Task.CompletedTask; });
        await Case("native exchange rejects renamed, malformed and padded requests", async f =>
        {
            File.Move(f.RequestPath, Path.Combine(f.Root, "Exchange", "other.request")); Check(f.Exchange.ReadCurrentChallenge() == null);
            File.Delete(Path.Combine(f.Root, "Exchange", "other.request")); File.WriteAllBytes(f.RequestPath, f.Body.Concat(new byte[] { 0 }).ToArray()); Check(f.Exchange.ReadCurrentChallenge() == null); await Task.CompletedTask;
        });
        await Case("native exchange never overwrites preexisting approval", async f =>
        {
            var p = await f.Poll(); byte[] sentinel = [1, 2, 3]; File.WriteAllBytes(f.ApprovalPath, sentinel); using var r = await f.Decide(f.Decision(p));
            Check(r.StatusCode == HttpStatusCode.ServiceUnavailable && File.ReadAllBytes(f.ApprovalPath).SequenceEqual(sentinel) && !File.Exists(f.RequestPath + ".temporary"));
        });
        await Case("native exchange refuses changed context before file submission", async f =>
        { f.Current = false; Check(!f.Exchange.Submit(f.Body, WindowsGrantWire.Sign(WindowsGrantWire.ReadChallenge(f.Body), f.Phone)) && !File.Exists(f.ApprovalPath)); await Task.CompletedTask; });
        await Case("native exchange bounds untrusted directory entries", async f =>
        {
            for (int i = 0; i < 65; i++) File.WriteAllBytes(Path.Combine(f.Root, "Exchange", i + ".request"), []);
            using var r = await f.Client.PostAsJsonAsync("/v2/windows/poll", f.PollRequest()); Check(r.StatusCode == HttpStatusCode.ServiceUnavailable);
        });
        await Case("native exchange missing directory returns availability error", async f =>
        { File.Delete(f.RequestPath); Directory.Delete(Path.Combine(f.Root, "Exchange")); using var r = await f.Client.PostAsJsonAsync("/v2/windows/poll", f.PollRequest()); Check(r.StatusCode == HttpStatusCode.ServiceUnavailable); });
        await test("fixed phone HTTPS refreshes an expired request without creating an unlock approval",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();f.Desktop.Locked=false;
            var poll=f.PollRequest();poll=poll with{Signature=Protocol.Sign(f.Phone,FixedPhoneWire.ContextPollText(poll))};
            using var contextResponse=await f.Client.PostAsJsonAsync("/v2/windows/fixed/context",poll);
            contextResponse.EnsureSuccessStatusCode();var context=(await contextResponse.Content.ReadFromJsonAsync<FixedPhoneContext>())!;
            long sequence=0;
            async Task<FixedPhoneResult> State(bool? connected){
                var p=new BandConnectionReport(f.Relay.PcId,f.Device.DeviceId,context.ContextNonce,++sequence,f.Wall,new string('a',64),connected,"");
                p=p with{Signature=Protocol.Sign(f.Phone,FixedPhoneWire.ReportText(p))};
                using var r=await f.Client.PostAsJsonAsync("/v2/windows/fixed/state",p);r.EnsureSuccessStatusCode();
                return (await r.Content.ReadFromJsonAsync<FixedPhoneResult>())!;
            }
            await State(true);f.Wall+=4000;f.Mono+=4000;
            Check((await State(true)).Renewal==null && f.Desktop.Locks==0 && !f.Fixed!.Snapshot.Revoked);
            f.Wall+=60000;f.Mono+=60000;f.Desktop.Locked=true;
            await State(true);f.Wall+=4000;f.Mono+=4000;
            var renewal=(await State(true)).Renewal??throw new Exception("Expected request renewal");
            var body=Convert.FromBase64String(renewal.Body);var request=WindowsRequestRenewalWire.Read(body);
            var signed=WindowsRequestRenewalWire.Sign(request,f.Phone);
            var decision=new RequestRenewalDecision(renewal.PcId,renewal.DeviceId,renewal.ContextNonce,renewal.Body,Convert.ToBase64String(signed.AsSpan(436)));
            using var submission=await f.Client.PostAsJsonAsync("/v2/windows/fixed/renew",decision);submission.EnsureSuccessStatusCode();
            Check(File.ReadAllBytes(Path.Combine(f.Root,"Exchange","request-renewal.bin")).SequenceEqual(signed) && !File.Exists(f.ApprovalPath));
            using var beforeNative=await f.Client.PostAsJsonAsync("/v2/windows/poll",f.PollRequest());Check(beforeNative.StatusCode==HttpStatusCode.NoContent);
            f.Wall++;f.Mono++;f.NewRequest();var prompt=await f.Poll();
            Check(prompt.Challenge==Convert.ToBase64String(f.Body));
            using var approve=await f.Decide(f.Decision(prompt));approve.EnsureSuccessStatusCode();
            Check(File.Exists(f.ApprovalPath) && f.Desktop.Locked==true);
        });
        await test("fixed phone disconnect blocks a previously issued manual approval over HTTPS",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();
            var poll=f.PollRequest();var c=f.Fixed!.GetContext(poll with{Signature=Protocol.Sign(f.Phone,FixedPhoneWire.ContextPollText(poll))});
            long sequence=0;
            FixedPhoneResult State(bool? connected){
                var p=new BandConnectionReport(f.Relay.PcId,f.Device.DeviceId,c.ContextNonce,++sequence,f.Wall,new string('a',64),connected,"");
                return f.Fixed.Observe(p with{Signature=Protocol.Sign(f.Phone,FixedPhoneWire.ReportText(p))});
            }
            State(true);f.Wall+=4000;f.Mono+=4000;var r=State(true).Renewal!;
            var raw=WindowsRequestRenewalWire.Sign(WindowsRequestRenewalWire.Read(Convert.FromBase64String(r.Body)),f.Phone);
            f.Fixed.Renew(new(r.PcId,r.DeviceId,r.ContextNonce,r.Body,Convert.ToBase64String(raw.AsSpan(436))));
            f.Wall++;f.Mono++;f.NewRequest();var prompt=await f.Poll();State(false);
            using var response=await f.Decide(f.Decision(prompt));Check(response.StatusCode==HttpStatusCode.Forbidden && !File.Exists(f.ApprovalPath));
        });
        await test("inline renewal returns a PC-signed new native challenge without approval",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();var renewal=await f.NearForRenewal();var poll=f.PollRequest();
            var native=Task.Run(async()=>{await Task.Delay(100);f.Wall++;f.Mono++;f.NewRequest();});
            using var response=await f.Client.PostAsJsonAsync("/v2/windows/fixed/renew",f.RenewalProof(renewal,poll));
            response.EnsureSuccessStatusCode();await native;var result=(await response.Content.ReadFromJsonAsync<FixedPhoneResult>())!;
            var prompt=result.Prompt??throw new Exception("Inline prompt missing");
            Check(result.Accepted && result.CanPoll && !result.WindowsUnlocked && prompt.Challenge==Convert.ToBase64String(f.Body) &&
                Protocol.Verify(Protocol.ExportPublicKey(f.Pc),WindowsGrantRelayWire.PromptText(prompt),prompt.Signature) && !File.Exists(f.ApprovalPath));
            using var replay=await f.Client.PostAsJsonAsync("/v2/windows/poll",poll);Check(replay.StatusCode==HttpStatusCode.Unauthorized);
        });
        await test("inline state preserves native and poll signatures",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();await f.ReadyFixedNative();
            var result=await f.FixedState(true,f.PollRequest());var prompt=result.Prompt??throw new Exception("Inline state prompt missing");
            Check(prompt.Challenge==Convert.ToBase64String(f.Body) &&
                Protocol.Verify(Protocol.ExportPublicKey(f.Pc),WindowsGrantRelayWire.PromptText(prompt),prompt.Signature) && !File.Exists(f.ApprovalPath));
        });
        await test("valid state proof cannot replace a forged inline poll signature",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();await f.ReadyFixedNative();
            var result=await f.FixedState(true,f.PollRequest() with{Signature="AAAA"});
            Check(result.Accepted && result.Prompt==null && !File.Exists(f.ApprovalPath));
        });
        await test("forged parent state cannot consume a valid inline poll nonce",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();await f.ReadyFixedNative();var poll=f.PollRequest();
            using var bad=await f.Client.PostAsJsonAsync("/v2/windows/fixed/state",f.StateProof(true,poll) with{Signature="AAAA"});
            Check(bad.StatusCode==HttpStatusCode.Forbidden);
            using var stillValid=await f.Client.PostAsJsonAsync("/v2/windows/poll",poll);Check(stillValid.IsSuccessStatusCode && !File.Exists(f.ApprovalPath));
        });
        await test("inline poll must independently match the enrolled phone",async()=>{
            await using var f=new Fixture(directory,true);await f.Start();await f.ReadyFixedNative();
            var poll=f.PollRequest() with{DeviceId=new string('b',64),Signature=""};
            poll=poll with{Signature=Protocol.Sign(f.Phone,WindowsGrantRelayWire.PollText(poll))};
            var result=await f.FixedState(true,poll);Check(result.Accepted && result.Prompt==null && !File.Exists(f.ApprovalPath));
        });
        await test("VM native policy rejects physical host without changing registry", () =>
        { Check(VmNativePolicy.ReadEnrolledPhone()==null); try { VmNativePolicy.RequireGuest(); } catch (InvalidOperationException) { return Task.CompletedTask; } throw new Exception("This host should fail the VM guard"); });
    }
}
