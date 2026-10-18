using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BandKey.Core;
using BandKey.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--phone-usb-lab")) return PhoneUsbLab.RunAsync(args).GetAwaiter().GetResult();
        if (args.Contains("--tray"))
        {
            // Separate harmless child process used exclusively by the watchdog recovery test.
            int index = Array.IndexOf(args, "--data");
            var path = Path.Combine(args[index + 1], "fixture-count.txt");
            var count = File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0;
            File.WriteAllText(path, (++count).ToString()); return count == 1 ? 17 : 0;
        }
        return Run(args).GetAwaiter().GetResult();
    }
    private static async Task<int> Run(string[] args)
    {
        var tests = new List<object>(); int failed = 0;
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); tests.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; tests.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e); }
        }
        var dir = Path.GetFullPath("artifacts/windows-test-" + Guid.NewGuid().ToString("N")); var store = new Store(dir);
        await Test("DPAPI signing key survives agent restart", () =>
        {
            using var k = store.LoadSigningKey(); using var restored = store.LoadSigningKey();
            Assert(Protocol.ExportPublicKey(k) == Protocol.ExportPublicKey(restored));
            Assert(!File.ReadAllText(Path.Combine(dir, "identity.dpapi")).Contains("PRIVATE KEY")); return Task.CompletedTask;
        });
        await Test("DPAPI TLS identity persists", () =>
        {
            using var a = store.LoadTlsCertificate(); using var b = store.LoadTlsCertificate();
            Assert(a.HasPrivateKey && BridgeServer.CertificatePin(a) == BridgeServer.CertificatePin(b)); return Task.CompletedTask;
        });
        using var pc = store.LoadSigningKey(); using var phone = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var cert = store.LoadTlsCertificate();
        var pub = Protocol.ExportPublicKey(phone); var device = new PublicDevice(Protocol.Fingerprint(pub), "test", pub);
        var broker = new AuthorizationBroker("integration-pc", pc); broker.Enroll(device); broker.SetContext(true, true);
        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        await using var server = new BridgeServer();
        await server.StartAsync(new AppSettings { Port = port }, cert, broker, _ => { });
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && BridgeServer.CertificatePin(c) == BridgeServer.CertificatePin(cert) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        PollRequest Poll() { var p = new PollRequest("integration-pc", device.DeviceId, Protocol.Nonce(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ""); return p with { Signature = Protocol.Sign(phone, Protocol.PollText(p)) }; }
        Challenge? challenge = null;
        await Test("HTTPS pinned signed poll produces PC-signed challenge", async () =>
        {
            using var response = await client.PostAsJsonAsync("/v1/poll", Poll()); Assert(response.IsSuccessStatusCode);
            challenge = await response.Content.ReadFromJsonAsync<Challenge>(); Assert(challenge != null && Protocol.Verify(broker.PcPublicKey, Protocol.ChallengeText(challenge), challenge.Signature));
        });
        await Test("HTTPS wearable approval, replay rejected, no Windows unlock claim", async () =>
        {
            var a = new Approval(challenge!, true, "wearengine-button", ""); a = a with { Signature = Protocol.Sign(phone, Protocol.ApprovalText(a)) };
            using var response = await client.PostAsJsonAsync("/v1/approval", a); var result = await response.Content.ReadFromJsonAsync<ApprovalResult>();
            Assert(response.IsSuccessStatusCode && result!.Authorized && !result.WindowsUnlocked);
            using var replay = await client.PostAsJsonAsync("/v1/approval", a); Assert(replay.StatusCode == HttpStatusCode.Forbidden);
        });
        await Test("HTTPS rejects untrusted poll", async () =>
        { using var response = await client.PostAsJsonAsync("/v1/poll", Poll() with { Signature = "AAAA" }); Assert(response.StatusCode == HttpStatusCode.Unauthorized); });
        await Test("HTTPS no configuration / enrollment route", async () =>
        { using var response = await client.PostAsJsonAsync("/v1/enroll", device); Assert(response.StatusCode == HttpStatusCode.NotFound); });
        await Test("normal agent does not expose VM native grant routes", async () =>
        { using var response = await client.PostAsJsonAsync("/v2/windows/poll", Poll()); Assert(response.StatusCode == HttpStatusCode.NotFound); });
        await Test("malformed JSON is rejected", async () =>
        { using var response = await client.PostAsync("/v1/approval", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")); Assert((int)response.StatusCode is >= 400 and < 500); });
        await Test("HTTPS refuses a wrong certificate pin", async () =>
        {
            using var wrong = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => false });
            try { await wrong.PostAsJsonAsync(client.BaseAddress + "v1/poll", Poll()); } catch (HttpRequestException) { return; }
            throw new Exception("Wrong certificate pin unexpectedly accepted");
        });
        await Test("settings roundtrip keeps default auto-lock disabled", () =>
        { store.Write("settings.json", new AppSettings()); Assert(store.Read<AppSettings>("settings.json") is { AutoLock: false, BridgeEnabled: false }); return Task.CompletedTask; });
        await Test("Android production Java wire code and .NET verify each other's P256 signatures", async () =>
        {
            var input = Path.Combine(dir, "interop-input.txt"); var output = Path.Combine(dir, "interop-output.txt");
            var text = Protocol.ChallengeText(challenge!);
            var expiredRequest = new CertificateRequest("CN=ExpiredBandKeyTest", pc, HashAlgorithmName.SHA256);
            using var expiredCertificate = expiredRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
            File.WriteAllLines(input, [broker.PcPublicKey, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)), challenge!.Signature,
                Convert.ToBase64String(cert.RawData), BridgeServer.CertificatePin(cert), Convert.ToBase64String(expiredCertificate.RawData), BridgeServer.CertificatePin(expiredCertificate)]);
            var classes = Path.Combine(dir, "java-classes"); Directory.CreateDirectory(classes);
            async Task RunTool(string exe, params string[] arguments)
            {
                var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in arguments) info.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); await stdout;
                if (process.ExitCode != 0) throw new Exception(await stderr);
            }
            await RunTool("javac", "-encoding", "UTF-8", "-d", classes, "android/app/src/main/java/dev/bandkey/bridge/Wire.java", "android/app/src/main/java/dev/bandkey/bridge/CertificatePinTrust.java", "tests/interop/ProtocolInterop.java");
            await RunTool("java", "-cp", classes, "ProtocolInterop", input, output);
            var result = File.ReadAllLines(output); Assert(Protocol.Verify(result[0], text, result[1]));
        });
        await Test("watchdog restarts failed process and respects a subsequent clean exit", async () =>
        {
            var state = Path.Combine(dir, "watchdog state"); Directory.CreateDirectory(state);
            var fixture = Path.Combine(AppContext.BaseDirectory, "BandKey.Windows.Tests.exe");
            var info = new System.Diagnostics.ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-File", Path.GetFullPath("scripts/watchdog.ps1"), "-Executable", fixture, "-DataDirectory", state, "-MaxRestarts", "3" }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            await stdout; if (process.ExitCode != 0) throw new Exception(await stderr);
            Assert(File.ReadAllText(Path.Combine(state, "fixture-count.txt")) == "2");
        });
        await WindowsGrantRelayIntegrationTests.RunAsync(Test, dir);
        await UsbTunnelIntegrationTests.RunAsync(Test);
        var report = new { generatedAt = DateTimeOffset.UtcNow, total = tests.Count, passed = tests.Count - failed, failed, tests };
        File.WriteAllText(args.FirstOrDefault() ?? "artifacts/windows-tests.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); return failed == 0 ? 0 : 1;
    }
}
