using System.Security.Cryptography;
using BandKey.Core;

internal static class AuthenticationTests
{
    private sealed class Backend : IWindowsAuthenticationBackend
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsSimulation { get; set; } = true;
        public int Calls;
        public Func<AuthenticationRequest, CancellationToken, Task<AuthenticationResult>> Run = (_, _) => Task.FromResult(new AuthenticationResult(true, "test"));
        public Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken token) { Calls++; return Run(request, token); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly ECDsa Pc = ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly AuthorizationBroker Broker;
        public readonly PublicDevice Device;
        public readonly Backend Backend = new();
        public long Now = 1000;
        public bool? Locked = true;
        public Fixture(string source = ConfirmationSources.WearEngine, bool testOnly = false)
        {
            var pub = Protocol.ExportPublicKey(DeviceKey); Device = new(Protocol.Fingerprint(pub), "test", pub, source, testOnly);
            Broker = new("test-pc", Pc); Broker.Enroll(Device); Broker.SetContext(true, true);
        }
        public Approval Approval(bool approved = true, string? source = null)
        {
            var p = new PollRequest("test-pc", Device.DeviceId, Protocol.Nonce(), Now, ""); p = p with { Signature = Protocol.Sign(DeviceKey, Protocol.PollText(p)) };
            var c = Broker.Poll(p, Now)!; var a = new Approval(c, approved, source ?? Device.ConfirmationSource, "");
            return a with { Signature = Protocol.Sign(DeviceKey, Protocol.ApprovalText(a)) };
        }
        public AuthenticationCoordinator Flow(TimeSpan? timeout = null) => new(Broker, Backend, () => Locked, () => Now, timeout);
        public void Dispose() { DeviceKey.Dispose(); Pc.Dispose(); }
    }
    private static void Assert(bool ok) { if (!ok) throw new Exception("Unexpected authentication flow outcome"); }
    public static void Run(Action<string, Action> test)
    {
        void Case(string name, Func<Task> body) => test(name, () => body().GetAwaiter().GetResult());
        Case("signed confirmation reaches backend with bound request", async () =>
        {
            using var f = new Fixture(); var a = f.Approval();
            f.Backend.Run = (request, _) => { Assert(request.Challenge == a.Challenge && request.Device == f.Device && request.Source == ConfirmationSources.WearEngine); return Task.FromResult(new AuthenticationResult(true, "simulated")); };
            var r = await f.Flow().AcceptAsync(a); Assert(r.Authorized && r.Simulated && !r.WindowsUnlocked && f.Backend.Calls == 1);
        });
        Case("concurrent approval replay invokes backend once", async () =>
        {
            using var f = new Fixture(); var a = f.Approval();var flow = f.Flow();
            var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => flow.AcceptAsync(a))));
            Assert(results.Count(r => r.Authorized) == 1 && f.Backend.Calls == 1);
        });
        Case("rejection never invokes authentication backend", async () =>
        { using var f = new Fixture(); var r = await f.Flow().AcceptAsync(f.Approval(false)); Assert(!r.Authorized && f.Backend.Calls == 0); });
        Case("forged approval never invokes authentication backend", async () =>
        { using var f = new Fixture(); var r = await f.Flow().AcceptAsync(f.Approval() with { Signature = "AAAA" }); Assert(!r.Authorized && f.Backend.Calls == 0); });
        Case("expired challenge never invokes authentication backend", async () =>
        { using var f = new Fixture(); var a = f.Approval(); f.Now += 30; var r = await f.Flow().AcceptAsync(a); Assert(!r.Authorized && f.Backend.Calls == 0); });
        Case("revoked device never invokes authentication backend", async () =>
        { using var f = new Fixture(); var a = f.Approval(); f.Broker.Revoke(f.Device.DeviceId); var r = await f.Flow().AcceptAsync(a); Assert(!r.Authorized && f.Backend.Calls == 0); });
        Case("confirmation source is bound to enrollment", async () =>
        { using var f = new Fixture(); var r = await f.Flow().AcceptAsync(f.Approval(source: ConfirmationSources.TestClient)); Assert(!r.Authorized && f.Backend.Calls == 0); });
        Case("test-only client cannot invoke a live backend", async () =>
        { using var f = new Fixture(ConfirmationSources.TestClient, true); f.Backend.IsSimulation = false; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(r.Authorized && r.Simulated && !r.WindowsUnlocked && f.Backend.Calls == 0); });
        Case("unavailable backend preserves authorization without unlock", async () =>
        { using var f = new Fixture(); f.Backend.IsAvailable = false; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(r.Authorized && !r.WindowsUnlocked && r.Code == "authentication-backend-unavailable" && f.Backend.Calls == 0); });
        Case("backend exception is contained without leaking error contents", async () =>
        { using var f = new Fixture(); f.Backend.Run = (_, _) => throw new Exception("private-test-detail"); var r = await f.Flow().AcceptAsync(f.Approval()); Assert(r.Code == "authentication-backend-error" && !r.WindowsUnlocked && !r.Code.Contains("private")); });
        Case("backend timeout cancels request without unlock", async () =>
        { using var f = new Fixture(); f.Backend.Run = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(true, "never"); }; var r = await f.Flow(TimeSpan.FromMilliseconds(30)).AcceptAsync(f.Approval()); Assert(r.Code == "authentication-timeout" && !r.WindowsUnlocked); });
        Case("cancelled request cannot start backend", async () =>
        { using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel(); var r = await f.Flow().AcceptAsync(f.Approval(), cancel.Token); Assert(r.Code == "authentication-cancelled" && f.Backend.Calls == 0); });
        Case("backend success alone is not Windows unlock", async () =>
        { using var f = new Fixture(); f.Backend.IsSimulation = false; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(r.Authorized && !r.WindowsUnlocked && r.Code == "awaiting-windows-session-confirmation"); });
        Case("unknown session cannot confirm Windows unlock", async () =>
        { using var f = new Fixture(); f.Locked = null; f.Backend.IsSimulation = false; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(!r.WindowsUnlocked); });
        Case("revocation during backend execution invalidates completion", async () =>
        { using var f = new Fixture(); f.Backend.IsSimulation = false; f.Backend.Run = (_, _) => { f.Broker.Revoke(f.Device.DeviceId); f.Locked = false; return Task.FromResult(new AuthenticationResult(true, "test")); }; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(!r.WindowsUnlocked && r.Code == "authentication-context-changed"); });
        Case("device policy replacement during backend invalidates completion", async () =>
        { using var f = new Fixture(); f.Backend.IsSimulation = false; f.Backend.Run = (_, _) => { f.Broker.Enroll(f.Device with { Name = "replacement" }); f.Locked = false; return Task.FromResult(new AuthenticationResult(true, "test")); }; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(!r.WindowsUnlocked && r.Code == "authentication-context-changed"); });
        Case("new lock session during backend invalidates completion", async () =>
        { using var f = new Fixture(); f.Backend.IsSimulation = false; f.Backend.Run = (_, _) => { f.Broker.SetContext(false, true); f.Broker.SetContext(true, true); f.Locked = false; return Task.FromResult(new AuthenticationResult(true, "test")); }; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(!r.WindowsUnlocked && r.Code == "authentication-context-changed"); });
        Case("expired backend completion cannot report Windows unlock", async () =>
        { using var f = new Fixture(); f.Backend.IsSimulation = false; f.Backend.Run = (_, _) => { f.Now += 30; f.Locked = false; return Task.FromResult(new AuthenticationResult(true, "test")); }; var r = await f.Flow().AcceptAsync(f.Approval()); Assert(!r.WindowsUnlocked && r.Code == "authentication-context-changed"); });
        test("accepted device policy is captured atomically with authorization", () =>
        { using var f = new Fixture(); var r = f.Broker.Accept(f.Approval(), f.Now, out var accepted); f.Broker.Enroll(f.Device with { Name = "replacement" }); Assert(r.Authorized && accepted == f.Device && !f.Broker.IsDeviceCurrent(accepted!)); });
        foreach (var source in new[] { ConfirmationSources.DirectBle, ConfirmationSources.Gadgetbridge })
            Case("signed source adapter contract: " + source, async () =>
            { using var f = new Fixture(source); var r = await f.Flow().AcceptAsync(f.Approval()); Assert(r.Authorized && r.Simulated && !r.WindowsUnlocked); });
        test("test client enrollment requires test-only flag", () =>
        { try { using var f = new Fixture(ConfirmationSources.TestClient); } catch (ArgumentException) { return; } throw new Exception("Unsafe test enrollment accepted"); });
        test("legacy Android enrollment retains Wear Engine source", () =>
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<PublicDevice>("{\"deviceId\":\"d\",\"name\":\"n\",\"publicKey\":\"p\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert(d is { ConfirmationSource: ConfirmationSources.WearEngine, TestOnly: false });
        });
    }
}
