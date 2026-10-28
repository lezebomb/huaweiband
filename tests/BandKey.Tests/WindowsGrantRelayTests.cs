using BandKey.Core;
using System.Security.Cryptography;

internal static class WindowsGrantRelayTests
{
    private sealed class Exchange : IWindowsGrantExchange
    {
        public byte[]? Current, Last;
        public int Submissions;
        public bool Accept = true;
        public bool ThrowOnSubmit;
        public byte[]? ReadCurrentChallenge() => Current?.ToArray();
        public bool Submit(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> approval) { if (ThrowOnSubmit) throw new IOException("synthetic exchange unavailable"); if (!Accept || Current == null || !expected.SequenceEqual(Current)) return false; Last = approval.ToArray(); Submissions++; return true; }
    }
    private sealed class Fixture : IDisposable
    {
        public ECDsa Pc = ECDsa.Create(ECCurve.NamedCurves.nistP256), Phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public long Wall = 1700000000000, Mono = 1000;
        public bool Context = true;
        public Exchange Exchange = new();
        public PublicDevice Device;
        public WindowsGrantRelay Relay;
        public Fixture(ulong lifetime = 30000)
        {
            var pub = Protocol.ExportPublicKey(Phone); Device = new(Protocol.Fingerprint(pub), "VM test", pub, ConfirmationSources.TestClient, true);
            ulong now = WindowsGrantRelayWire.FileTimeMilliseconds(Wall);
            Exchange.Current = WindowsGrantWire.WriteChallenge(new(5, now, now + lifetime, "S-1-5-21-1-2-3-1001", Protocol.Nonce()));
            Relay = new(Pc, Device, Exchange, () => Context, () => Wall, () => Mono);
        }
        public PollRequest Poll() { var p = new PollRequest(Relay.PcId, Device.DeviceId, Protocol.Nonce(), Wall / 1000, ""); return p with { Signature = Protocol.Sign(Phone, WindowsGrantRelayWire.PollText(p)) }; }
        public WindowsGrantPrompt Prompt() => Relay.Poll(Poll()) ?? throw new Exception("No prompt");
        public WindowsGrantDecision Decision(bool approved = true)
        {
            var prompt = Prompt(); var d = new WindowsGrantDecision(Relay.PcId, Device.DeviceId, prompt.Challenge, approved, "");
            if (!approved) return d with { Signature = Protocol.Sign(Phone, WindowsGrantRelayWire.DenialText(d)) };
            var approval = WindowsGrantWire.Sign(WindowsGrantWire.ReadChallenge(Convert.FromBase64String(prompt.Challenge)), Phone);
            return d with { Signature = Convert.ToBase64String(approval.AsSpan(436)) };
        }
        public void Dispose() { Pc.Dispose(); Phone.Dispose(); }
    }
    public static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Relay invariant"); }
        void Refuses(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected refusal"); }
        test("two minute relay accepts delayed explicit confirmation only once", () => {
            using var f = new Fixture(120000); var d = f.Decision(); f.Wall += 90000; f.Mono += 90000;
            Check(f.Relay.Decide(d).Submitted && !f.Relay.Decide(d).Submitted && f.Exchange.Submissions == 1);
        });
        foreach (bool freezeWall in new[] { false, true }) test("two minute relay expires on " + (freezeWall ? "monotonic" : "wall clock") + " boundary", () => {
            using var f = new Fixture(120000); var d = f.Decision(); if (!freezeWall) f.Wall += 120000; f.Mono += 120000;
            Check(!f.Relay.Decide(d).Submitted && f.Exchange.Submissions == 0);
        });
        test("unsupported grant lifetimes are refused", () => { Refuses(() => { using var f = new Fixture(121000); }); Refuses(() => { using var f = new Fixture(60000); }); });
        test("native relay signed poll returns a PC-signed native prompt", () => { using var f = new Fixture(); var p = f.Prompt(); Check(Protocol.Verify(Protocol.ExportPublicKey(f.Pc), WindowsGrantRelayWire.PromptText(p), p.Signature)); });
        test("native relay submits exact phone P1363 proof without unlock claim", () => { using var f = new Fixture(); var d = f.Decision(); f.Prompt(); var r = f.Relay.Decide(d); Check(r is { Authorized: true, Submitted: true, WindowsUnlocked: false, TestOnly: true } && f.Exchange.Submissions == 1 && f.Relay.Activity is { Prompts: 1, Decisions: 1, ApprovalFilesSubmitted: 1, SignedDenials: 0, Rejections: 0 }); });
        test("native relay rejects poll replay", () => { using var f = new Fixture(); var p = f.Poll(); f.Relay.Poll(p); Refuses(() => f.Relay.Poll(p)); });
        test("legacy poll signatures cannot enter native relay", () => { using var f = new Fixture(); var p = f.Poll(); p = p with { Signature = Protocol.Sign(f.Phone, Protocol.PollText(p)) }; Refuses(() => f.Relay.Poll(p)); });
        test("native relay rejects unknown polling identity", () => { using var f = new Fixture(); Refuses(() => f.Relay.Poll(f.Poll() with { DeviceId = "unknown" })); });
        test("native relay rejects response replay", () => { using var f = new Fixture(); var d = f.Decision(); f.Relay.Decide(d); Check(!f.Relay.Decide(d).Submitted && f.Exchange.Submissions == 1 && f.Relay.Poll(f.Poll()) == null); });
        test("native relay concurrent approvals submit once", () => { using var f = new Fixture(); var d = f.Decision(); var results = Enumerable.Range(0, 20).Select(_ => Task.Run(() => f.Relay.Decide(d))).ToArray(); Task.WaitAll(results); Check(results.Count(t => t.Result.Submitted) == 1 && f.Exchange.Submissions == 1); });
        test("native relay rejects changed native nonce", () => { using var f = new Fixture(); var d = f.Decision(); f.Exchange.Current![36] ^= 1; Check(!f.Relay.Decide(d).Submitted && f.Exchange.Submissions == 0); });
        test("native relay rejects changed wire body", () => { using var f = new Fixture(); var d = f.Decision(); var b = Convert.FromBase64String(d.Challenge); b[16] ^= 1; Check(!f.Relay.Decide(d with { Challenge = Convert.ToBase64String(b) }).Submitted); });
        test("native relay rejects forged signature", () => { using var f = new Fixture(); var d = f.Decision(); var s = Convert.FromBase64String(d.Signature); s[0] ^= 1; Check(!f.Relay.Decide(d with { Signature = Convert.ToBase64String(s) }).Submitted && f.Exchange.Submissions == 0); });
        test("native relay rejects null signature without throwing", () => { using var f = new Fixture(); Check(!f.Relay.Decide(f.Decision() with { Signature = null! }).Submitted); });
        test("native relay rejects truncated signature", () => { using var f = new Fixture(); Check(!f.Relay.Decide(f.Decision() with { Signature = "AAAA" }).Submitted); });
        test("native relay rejects expired native challenge", () => { using var f = new Fixture(); var d = f.Decision(); f.Wall += 30000; Check(!f.Relay.Decide(d).Submitted); });
        test("native relay rejects monotonic expiry despite wall clock", () => { using var f = new Fixture(); var d = f.Decision(); f.Mono += 30000; Check(!f.Relay.Decide(d).Submitted); });
        test("native relay wall rollback invalidates instance", () => { using var f = new Fixture(); var d = f.Decision(); f.Wall--; Check(!f.Relay.Decide(d).Submitted); f.Wall++; Refuses(() => f.Prompt()); });
        test("native relay context loss invalidates pending proof", () => { using var f = new Fixture(); var d = f.Decision(); f.Context = false; Check(!f.Relay.Decide(d).Submitted); f.Context = true; Refuses(() => f.Prompt()); });
        test("native relay explicit revocation prevents submission", () => { using var f = new Fixture(); var d = f.Decision(); f.Relay.Revoke(); Check(!f.Relay.Decide(d).Submitted); });
        test("native relay signed denial consumes request without native write", () => { using var f = new Fixture(); var d = f.Decision(false); Check(f.Relay.Decide(d).Code == "user-denied" && f.Exchange.Submissions == 0 && f.Relay.Poll(f.Poll()) == null && f.Relay.Activity is { Decisions: 1, ApprovalFilesSubmitted: 0, SignedDenials: 1, Rejections: 0 }); });
        test("native relay unsigned denial cannot consume request", () => { using var f = new Fixture(); var d = f.Decision(false); Check(f.Relay.Decide(d with { Signature = "AAAA" }).Code == "relay-denial-invalid"); Check(f.Relay.Decide(f.Decision()).Submitted); });
        test("native exchange failure never reports success or retries", () => { using var f = new Fixture(); var d = f.Decision(); f.Exchange.Accept = false; var r = f.Relay.Decide(d); Check(r is { Authorized: true, Submitted: false, WindowsUnlocked: false } && !f.Relay.Decide(d).Submitted); });
        test("native exchange I/O fault is recorded and cannot reuse authorization", () =>
        {
            using var f = new Fixture(); var d = f.Decision(); f.Exchange.ThrowOnSubmit = true;
            Refuses(() => f.Relay.Decide(d)); Check(f.Relay.Activity is { Decisions: 1, Rejections: 1, ApprovalFilesSubmitted: 0, LastResult: "native-exchange-io-error" });
            f.Exchange.ThrowOnSubmit = false; Check(!f.Relay.Decide(d).Submitted && f.Exchange.Submissions == 0);
        });
        test("native request disappearance cannot submit proof", () => { using var f = new Fixture(); var d = f.Decision(); f.Exchange.Current = null; Check(!f.Relay.Decide(d).Submitted); });
        test("unlocked native exchange sends no challenge", () => { using var f = new Fixture(); f.Exchange.Current = null; Check(f.Relay.Poll(f.Poll()) == null); });
        test("native relay requires explicit test-only enrollment", () => { using var f = new Fixture(); Refuses(() => new WindowsGrantRelay(f.Pc, f.Device with { TestOnly = false }, f.Exchange, () => true)); });
        test("native public key codec matches SPKI identity", () => { using var f = new Fixture(); var native = WindowsGrantWire.ExportNativePublicKey(f.Phone); using var imported = WindowsGrantWire.ImportNativePublicKey(native); Check(Protocol.ExportPublicKey(imported) == f.Device.PublicKey); });
    }
}
