using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BandKey.Core;

namespace BandKey.Windows;

// This entry point has no WindowsSession/LockWorkStation calls and never loads agent settings.
internal static class FlowLab
{
    private sealed class Backend : IWindowsAuthenticationBackend
    {
        public bool IsAvailable => true;
        public bool IsSimulation => true;
        public int Calls;
        public Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new AuthenticationResult(true, "simulation-only")); }
    }
    private sealed class Confirmation(ECDsa key) : IUserConfirmationSource
    {
        public string Source => ConfirmationSources.TestClient;
        public ValueTask<Approval?> ConfirmAsync(Challenge challenge, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var a = new Approval(challenge, true, Source, "");
            return ValueTask.FromResult<Approval?>(a with { Signature = Protocol.Sign(key, Protocol.ApprovalText(a)) });
        }
    }
    public static async Task<int> RunAsync(string output)
    {
        var results = new List<object>(); int failures = 0;
        async Task Check(string name, Func<Task<bool>> test)
        {
            bool passed; try { passed = await test(); } catch { passed = false; }
            if (!passed) failures++; results.Add(new { name, passed });
        }
        var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new Store(Path.Combine(Path.GetDirectoryName(path)!, "flow-lab-" + Guid.NewGuid().ToString("N")));
        using var pc = store.LoadSigningKey(); using var cert = store.LoadTlsCertificate(); using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pcId = Protocol.Fingerprint(Protocol.ExportPublicKey(pc)); var publicKey = Protocol.ExportPublicKey(clientKey);
        var device = new PublicDevice(Protocol.Fingerprint(publicKey), "isolated-test-client", publicKey, ConfirmationSources.TestClient, true);
        var broker = new AuthorizationBroker(pcId, pc); broker.Enroll(device);
        var backend = new Backend(); var flow = new AuthenticationCoordinator(broker, backend, () => true);
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var server = new BridgeServer();
        await server.StartAsync(new AppSettings { Port = port }, cert, broker, _ => { }, authentication: flow);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null && BridgeServer.CertificatePin(certificate) == BridgeServer.CertificatePin(cert) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        PollRequest Poll()
        {
            var p = new PollRequest(pcId, device.DeviceId, Protocol.Nonce(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "");
            return p with { Signature = Protocol.Sign(clientKey, Protocol.PollText(p)) };
        }
        await Check("unlocked simulated session sends no challenge", async () =>
        { broker.SetContext(false, true); using var r = await client.PostAsJsonAsync("/v1/poll", Poll()); return r.StatusCode == HttpStatusCode.NoContent; });
        await Check("unknown simulated proximity sends no challenge", async () =>
        { broker.SetContext(true, false); using var r = await client.PostAsJsonAsync("/v1/poll", Poll()); return r.StatusCode == HttpStatusCode.NoContent; });
        var presence = new PresenceMonitor(new PresenceOptions { NearSeconds = 1 });
        PresenceSnapshot? snapshot = null; for (int i = 0; i < 8; i++) snapshot = presence.Observe(-45, i);
        broker.SetContext(true, snapshot!.State == PresenceState.Near);
        Challenge? challenge = null;
        await Check("simulated near and locked yields a verified PC challenge", async () =>
        {
            using var r = await client.PostAsJsonAsync("/v1/poll", Poll()); challenge = await r.Content.ReadFromJsonAsync<Challenge>();
            return r.IsSuccessStatusCode && challenge != null && Protocol.Verify(broker.PcPublicKey, Protocol.ChallengeText(challenge), challenge.Signature);
        });
        await Check("delivery without a click never invokes backend", () => Task.FromResult(backend.Calls == 0));
        var source = new Confirmation(clientKey); var approval = challenge == null ? null : await source.ConfirmAsync(challenge, default);
        await Check("wrong source is rejected even with a valid client signature", async () =>
        {
            if (approval == null) return false; var a = approval with { Source = ConfirmationSources.WearEngine }; a = a with { Signature = Protocol.Sign(clientKey, Protocol.ApprovalText(a)) };
            using var r = await client.PostAsJsonAsync("/v1/approval", a); return r.StatusCode == HttpStatusCode.Forbidden && backend.Calls == 0;
        });
        await Check("signed test click reaches simulation backend without unlocking Windows", async () =>
        {
            using var r = await client.PostAsJsonAsync("/v1/approval", approval); var value = await r.Content.ReadFromJsonAsync<ApprovalResult>();
            return r.IsSuccessStatusCode && value is { Authorized: true, Simulated: true, WindowsUnlocked: false } && backend.Calls == 1;
        });
        await Check("network replay cannot invoke backend again", async () =>
        { using var r = await client.PostAsJsonAsync("/v1/approval", approval); return r.StatusCode == HttpStatusCode.Forbidden && backend.Calls == 1; });
        await Check("network enrollment and remote configuration remain unavailable", async () =>
        { using var r = await client.PostAsJsonAsync("/v1/enroll", device); return r.StatusCode == HttpStatusCode.NotFound; });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, simulated = true, realWindowsAuthenticationTested = false,
            realWearableTested = false, transport = "actual-pinned-loopback-https", total = results.Count, passed = results.Count - failures, failed = failures, tests = results }, Store.Json));
        return failures == 0 ? 0 : 1;
    }
}
