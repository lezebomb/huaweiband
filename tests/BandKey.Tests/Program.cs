using System.Security.Cryptography;
using System.Text.Json;
using BandKey.Core;

if(args.Length==2 && args[0] is "--ble-vectors" or "--verify-ble-java")return BleCodecInterop.Run(args[0],Path.GetFullPath(args[1]));
var tests = new List<object>(); int failed = 0;
void Test(string name, Action action)
{
    try { action(); tests.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; tests.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Assert(bool value, string reason = "Assertion failed") { if (!value) throw new Exception(reason); }
void Throws(Action a) { try { a(); } catch { return; } throw new Exception("Expected rejection"); }
PresenceMonitor Monitor() => new(new PresenceOptions { MedianWindow = 3, Alpha = 1, NearSeconds = 1, FarSeconds = 3, StaleSeconds = 5, MissingSeconds = 12 });
void Arm(PresenceMonitor m) { for (int i = 0; i <= 4; i++) m.Observe(-50, i); }
Test("startup absence never locks", () => Assert(!Monitor().Tick(100, true).ShouldLock));
Test("startup weak signal never arms or locks", () => { var m = Monitor(); for (int i = 0; i < 40; i++) Assert(!m.Observe(-100, i).ShouldLock); });
Test("stable near arms", () => { var m = Monitor(); Arm(m); Assert(m.Tick(4, true).Armed); });
Test("one RSSI outlier does not lock", () => { var m = Monitor(); Arm(m); Assert(!m.Observe(-110, 5).ShouldLock); Assert(m.Observe(-50, 6).State == PresenceState.Near); });
Test("hysteresis band never locks", () => { var m = Monitor(); Arm(m); for (int i = 5; i < 100; i++) Assert(!m.Observe(-73, i).ShouldLock); });
Test("sustained far emits exactly one lock", () => { var m = Monitor(); Arm(m); int n = 0; for (int i = 5; i < 30; i++) if (m.Observe(-95, i).ShouldLock) n++; Assert(n == 1, $"Got {n}"); });
Test("return near re-arms next departure", () => { var m = Monitor(); Arm(m); for (int i = 5; i < 15; i++) m.Observe(-95, i); for (int i = 15; i < 25; i++) m.Observe(-50, i); int n = 0; for (int i = 25; i < 40; i++) if (m.Observe(-95, i).ShouldLock) n++; Assert(n == 1); });
Test("default signal silence is unknown without lock", () => { var m = Monitor(); Arm(m); var s = m.Tick(500, true); Assert(s.State == PresenceState.Unknown && !s.ShouldLock); });
Test("optional missing timeout locks once", () => { var m = new PresenceMonitor(new PresenceOptions { LockOnMissingSignal = true }); for (int i = 0; i < 10; i++) m.Observe(-45, i); Assert(m.Tick(60, true).ShouldLock); Assert(!m.Tick(61, true).ShouldLock); });
Test("adapter failure disarms, even with missing lock enabled", () => { var m = new PresenceMonitor(new PresenceOptions { LockOnMissingSignal = true }); for (int i = 0; i < 10; i++) m.Observe(-45, i); Assert(!m.Tick(90, false).ShouldLock); Assert(!m.Tick(100, true).Armed); });
Test("sparse samples cannot accumulate near dwell", () => { var m = Monitor(); for (int i = 0; i < 10; i++) Assert(!m.Observe(-50, i * 10).Armed); });
Test("sparse samples cannot accumulate far dwell", () => { var m = Monitor(); Arm(m); for (int i = 1; i < 10; i++) Assert(!m.Observe(-95, 4 + i * 10).ShouldLock); });
Test("manual unlock reset prevents relock until fresh near", () => { var m = Monitor(); Arm(m); m.Reset(); for (int i = 10; i < 50; i++) Assert(!m.Observe(-95, i).ShouldLock); });
Test("backward / NaN monotonic time rejected", () => { var m = Monitor(); m.Tick(10, true); Throws(() => m.Tick(9, true)); Throws(() => m.Tick(double.NaN, true)); });
Test("invalid configuration rejected", () => { Throws(() => new PresenceMonitor(new PresenceOptions { NearRssi = -85, FarRssi = -80 })); Throws(() => new PresenceMonitor(new PresenceOptions { Alpha = double.NaN })); });
Test("invalid RSSI cannot arm", () => { var m = Monitor(); for (int i = 0; i < 20; i++) Assert(!m.Observe(127, i).Armed); });
Test("noisy near trace remains safe", () => { var m = new PresenceMonitor(new()); var random = new Random(73); for (int i = 0; i < 300; i++) Assert(!m.Observe(i % 17 == 0 ? -110 : -55 + random.Next(-7, 8), i).ShouldLock); Assert(m.Tick(300, true).Armed); });

using var pc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = Protocol.ExportPublicKey(phone);
var device = new PublicDevice(Protocol.Fingerprint(publicKey), "test-phone", publicKey);
AuthorizationBroker Broker() { var b = new AuthorizationBroker("pc-test", pc); b.Enroll(device); b.SetContext(true, true); return b; }
PollRequest Poll(long now = 1000) { var p = new PollRequest("pc-test", device.DeviceId, Protocol.Nonce(), now, ""); return p with { Signature = Protocol.Sign(phone, Protocol.PollText(p)) }; }
Approval Approve(Challenge c, bool yes = true, string source = "wearengine-button", ECDsa? signer = null)
{ var a = new Approval(c, yes, source, ""); return a with { Signature = Protocol.Sign(signer ?? phone, Protocol.ApprovalText(a)) }; }
Test("P256 DER signature roundtrip", () => Assert(Protocol.Verify(publicKey, "canonical", Protocol.Sign(phone, "canonical"))));
Test("forged signature rejected", () => Assert(!Protocol.Verify(publicKey, "canonical", Protocol.Sign(attacker, "canonical"))));
Test("malformed signatures rejected", () => Assert(!Protocol.Verify(publicKey, "x", "not-base64")));
Test("non-P256 curve rejected", () => { using var p = ECDsa.Create(ECCurve.NamedCurves.nistP384); Throws(() => Protocol.ReadPublicKey(Protocol.ExportPublicKey(p))); });
Test("signed challenge verified with PC public key", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(Protocol.Verify(b.PcPublicKey, Protocol.ChallengeText(c), c.Signature)); });
Test("valid wearable approval authorizes without claiming Windows unlock", () => { var b = Broker(); var r = b.Accept(Approve(b.Poll(Poll(), 1000)!), 1001); Assert(r.Authorized && !r.WindowsUnlocked && r.Code.Contains("unavailable")); });
Test("replay cannot authorize twice", () => { var b = Broker(); var a = Approve(b.Poll(Poll(), 1000)!); Assert(b.Accept(a, 1001).Authorized); Assert(!b.Accept(a, 1002).Authorized); });
Test("concurrent replay has exactly one winner", () => { var b = Broker(); var a = Approve(b.Poll(Poll(), 1000)!); int accepted = 0; Parallel.For(0, 50, _ => { if (b.Accept(a, 1001).Authorized) Interlocked.Increment(ref accepted); }); Assert(accepted == 1); });
Test("denial consumes challenge", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!b.Accept(Approve(c, false), 1001).Authorized); Assert(!b.Accept(Approve(c), 1002).Authorized); });
Test("expired approval rejected at boundary", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!b.Accept(Approve(c), c.ExpiresAt).Authorized); });
Test("approval before issued time rejected", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!b.Accept(Approve(c), 999).Authorized); });
Test("wrong phone key rejected", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!b.Accept(Approve(c, signer: attacker), 1001).Authorized); });
Test("send-success is not a button click", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!b.Accept(Approve(c, source: "send-success"), 1001).Authorized); });
Test("revocation immediately rejects pending challenge", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; b.Revoke(device.DeviceId); Assert(!b.Accept(Approve(c), 1001).Authorized); Throws(() => b.Poll(Poll(), 1002)); });
Test("PC restart invalidates challenge", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; Assert(!Broker().Accept(Approve(c), 1001).Authorized); });
Test("leaving near cancels pending request", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; b.SetContext(true, false); Assert(!b.Accept(Approve(c), 1001).Authorized); });
Test("new lock session invalidates old approval", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)!; b.SetContext(false, true); b.SetContext(true, true); Assert(!b.Accept(Approve(c), 1001).Authorized); });
Test("unlocked desktop issues no challenge", () => { var b = Broker(); b.SetContext(false, true); Assert(b.Poll(Poll(), 1000) == null); });
Test("uncertain proximity issues no challenge", () => { var b = Broker(); b.SetContext(true, false); Assert(b.Poll(Poll(), 1000) == null); });
Test("poll replay rejected", () => { var b = Broker(); var p = Poll(); b.Poll(p, 1000); Throws(() => b.Poll(p, 1001)); });
Test("stale polling request rejected", () => Throws(() => Broker().Poll(Poll(900), 1000)));
Test("wrong PC poll rejected", () => { var p = Poll() with { PcId = "wrong" }; p = p with { Signature = Protocol.Sign(phone, Protocol.PollText(p)) }; Throws(() => Broker().Poll(p, 1000)); });
Test("changed nonce rejected even with fresh phone signature", () => { var b = Broker(); var c = b.Poll(Poll(), 1000)! with { Nonce = Protocol.Nonce() }; Assert(!b.Accept(Approve(c), 1001).Authorized); });
Test("canonical field injection rejected", () => Throws(() => Protocol.PollText(Poll() with { PcId = "pc\nother" })));
Test("prompt rate limit after expiry", () => { var b = Broker(); Assert(b.Poll(Poll(), 1000) != null); Assert(b.Poll(Poll(1031), 1031) == null); Assert(b.Poll(Poll(1061), 1061) != null); });
Test("repeated fresh poll returns same pending prompt", () => { var b = Broker(); var a = b.Poll(Poll(), 1000); Assert(a == b.Poll(Poll(1001), 1001)); });
Test("authentication adapter fails closed", () => { var backend = new UnavailableAuthenticationBackend(); Assert(!backend.IsAvailable && !backend.AuthenticateAsync(null!, default).Result.WindowsAuthenticated); });

SafetyTests.Run(Test);
AuthenticationTests.Run(Test);
WindowsGrantRelayTests.Run(Test);
BlePipeTests.Run(Test);
var report = new { generatedAt = DateTimeOffset.UtcNow, total = tests.Count, passed = tests.Count - failed, failed, tests };
var output = args.FirstOrDefault() ?? "core-tests.json"; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;
