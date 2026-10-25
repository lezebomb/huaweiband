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
        internal Fixture(string directory,bool fixedPhone=false)
        {
            Root = Path.Combine(directory, "native-exchange-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(Root, "Exchange"));
            var publicKey = Protocol.ExportPublicKey(Phone); Device = new(Protocol.Fingerprint(publicKey), "VM integration test", publicKey, ConfirmationSources.TestClient, true);
            Exchange = new(Root, Sid, 5, () => Current, () => WindowsGrantRelayWire.FileTimeMilliseconds(Wall));
            if(fixedPhone) Fixed=new(Pc,Device,Exchange,Desktop,()=>Current,false,()=>Wall,()=>Mono);
            Relay = new(Pc, Device, Exchange, () => Current, () => Wall, () => Mono, proximityCurrent:()=>Fixed?.CanSubmit??true);
            Certificate = new Store(Path.Combine(Root, "pc-identity")).LoadTlsCertificate();
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && BridgeServer.CertificatePin(c) == BridgeServer.CertificatePin(Certificate) })
                { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            Port = port; NewRequest();
        }
        private int Port { get; }
        internal Task Start() => Server.StartAsync(new AppSettings { Port = Port }, Certificate, new AuthorizationBroker(Relay.PcId, Pc), _ => { }, windowsRelay: Relay,fixedPhone:Fixed);
        internal void NewRequest(string sid = Sid, uint session = 5)
        {
            ulong now = WindowsGrantRelayWire.FileTimeMilliseconds(Wall);
            Body = WindowsGrantWire.WriteChallenge(new(session, now, now + 30000, sid, Protocol.Nonce()));
            RequestPath = Path.Combine(Root, "Exchange", Convert.ToHexStringLower(Body.AsSpan(36, 32)) + ".request"); File.WriteAllBytes(RequestPath, Body);
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
        async Task Case(string name, Func<Fixture, Task> action) => await test(name, async () => { await using var f = new Fixture(directory); await f.Start(); await action(f); });
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
        await test("VM native policy rejects physical host without changing registry", () =>
        { try { VmNativePolicy.RequireGuest(); } catch (InvalidOperationException) { return Task.CompletedTask; } throw new Exception("This host should fail the VM guard"); });
    }
}
