using System.Security.Cryptography;

namespace BandKey.Core;

public sealed record WindowsGrantPrompt(string PcId, string DeviceId, string Challenge, string Signature);
public sealed record WindowsGrantDecision(string PcId, string DeviceId, string Challenge, bool Approved, string Signature);
public sealed record WindowsGrantRelayResult(bool Authorized, bool Submitted, bool WindowsUnlocked, string Code, bool TestOnly = true);
public sealed record WindowsGrantRelayActivity(long Prompts, long Decisions, long ApprovalFilesSubmitted, long SignedDenials, long Rejections, string LastResult, bool Revoked);

// An exchange never exposes Windows passwords. The native Provider owns and consumes its request.
public interface IWindowsGrantExchange
{
    byte[]? ReadCurrentChallenge();
    bool Submit(ReadOnlySpan<byte> expectedChallenge, ReadOnlySpan<byte> approval);
}

public static class WindowsGrantRelayWire
{
    public static string PollText(PollRequest poll) => ProtocolText("bandkey.windows-grant.poll.v1", poll.PcId, poll.DeviceId, poll.Nonce, poll.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string PromptText(WindowsGrantPrompt prompt) => ProtocolText("bandkey.windows-grant.prompt.v1", prompt.PcId, prompt.DeviceId, prompt.Challenge);
    public static string DenialText(WindowsGrantDecision decision) => ProtocolText("bandkey.windows-grant.deny.v1", decision.PcId, decision.DeviceId, decision.Challenge);
    public static string ProtocolText(params string[] fields)
    {
        if (fields.Any(value => string.IsNullOrEmpty(value) || value.Length > 2048 || value.Contains('\r') || value.Contains('\n'))) throw new FormatException("Windows relay field");
        return string.Join('\n', fields);
    }
    public static ulong FileTimeMilliseconds(long unixMilliseconds) => checked((ulong)checked(unixMilliseconds + 11644473600000L));
}

/// <summary>Signed relay with an independent native trust boundary. A successful submission is never an unlock result.</summary>
public sealed class WindowsGrantRelay
{
    private readonly object gate = new();
    private readonly ECDsa pc;
    private readonly PublicDevice device;
    private readonly byte[] nativeKey;
    private readonly IWindowsGrantExchange exchange;
    private readonly Func<long> unixMilliseconds;
    private readonly Func<long> monotonicMilliseconds;
    private readonly Func<bool> contextCurrent;
    private readonly Dictionary<string, long> polls = new(StringComparer.Ordinal);
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    private readonly Queue<string> consumedOrder = new();
    private byte[]? pending;
    private long pendingStarted, latestWall = long.MinValue;
    private bool revoked;
    private long prompts, decisions, submittedApprovals, signedDenials, rejections;
    private string lastResult = "not-started";
    public string PcId { get; }
    // Aggregate local diagnostics contain no account, device, nonce or signature.
    public WindowsGrantRelayActivity Activity { get { lock (gate) return new(prompts, decisions, submittedApprovals, signedDenials, rejections, lastResult, revoked); } }

    public WindowsGrantRelay(ECDsa pcKey, PublicDevice enrolled, IWindowsGrantExchange exchange,
        Func<bool> contextCurrent, Func<long>? unixMilliseconds = null, Func<long>? monotonicMilliseconds = null)
    {
        if (!enrolled.TestOnly || enrolled.ConfirmationSource != ConfirmationSources.TestClient) throw new ArgumentException("VM relay requires explicit test enrollment");
        using var key = Protocol.ReadPublicKey(enrolled.PublicKey);
        if (enrolled.DeviceId != Protocol.Fingerprint(enrolled.PublicKey)) throw new ArgumentException("Device fingerprint mismatch");
        nativeKey = WindowsGrantWire.ExportNativePublicKey(key);
        pc = pcKey; PcId = Protocol.Fingerprint(Protocol.ExportPublicKey(pc)); device = enrolled; this.exchange = exchange; this.contextCurrent = contextCurrent;
        this.unixMilliseconds = unixMilliseconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        this.monotonicMilliseconds = monotonicMilliseconds ?? (() => Environment.TickCount64);
    }
    public void Revoke() { lock (gate) { revoked = true; pending = null; } }
    private bool Current(long wall)
    {
        if (revoked) return false;
        if (!contextCurrent() || wall < latestWall) { revoked = true; pending = null; return false; }
        return true;
    }
    private void TrackWall(long wall) { if (wall > latestWall) latestWall = wall; }
    private bool PendingCurrent(long wall, long monotonic)
    {
        if (pending == null || monotonic < pendingStarted || monotonic - pendingStarted >= 30000) return false;
        return WindowsGrantWire.IsCurrent(WindowsGrantWire.ReadChallenge(pending), WindowsGrantRelayWire.FileTimeMilliseconds(wall));
    }
    public WindowsGrantPrompt? Poll(PollRequest poll)
    {
        lock (gate)
        {
            long wall = unixMilliseconds(), mono = monotonicMilliseconds();
            if (!Current(wall)) { pending = null; throw new UnauthorizedAccessException("relay-context-unavailable"); }
            TrackWall(wall);
            long now = wall / 1000;
            if (poll == null || poll.PcId != PcId || poll.DeviceId != device.DeviceId || !Protocol.ValidNonce(poll.Nonce) || poll.Timestamp < now - 30 || poll.Timestamp > now + 30 ||
                !Protocol.Verify(device.PublicKey, WindowsGrantRelayWire.PollText(poll), poll.Signature)) throw new UnauthorizedAccessException("relay-poll-invalid");
            foreach (var nonce in polls.Where(entry => entry.Value < now - 60).Select(entry => entry.Key).ToArray()) polls.Remove(nonce);
            if (polls.Count >= 4096 || !polls.TryAdd(poll.Nonce, now)) throw new UnauthorizedAccessException("relay-poll-replayed-or-full");
            byte[]? current = exchange.ReadCurrentChallenge();
            if (current == null) { pending = null; return null; }
            var parsed = WindowsGrantWire.ReadChallenge(current);
            if (!WindowsGrantWire.IsCurrent(parsed, WindowsGrantRelayWire.FileTimeMilliseconds(wall))) { pending = null; return null; }
            var body = Convert.ToBase64String(current);
            if (consumed.Contains(body)) return null;
            if (pending == null || !pending.AsSpan().SequenceEqual(current)) { pending = current.ToArray(); pendingStarted = mono; prompts++; }
            if (!PendingCurrent(wall, mono)) { pending = null; return null; }
            var prompt = new WindowsGrantPrompt(PcId, device.DeviceId, body, "");
            return prompt with { Signature = Protocol.Sign(pc, WindowsGrantRelayWire.PromptText(prompt)) };
        }
    }
    public WindowsGrantRelayResult Decide(WindowsGrantDecision decision)
    {
        lock (gate)
        {
            long wall = unixMilliseconds(), mono = monotonicMilliseconds();
            if (!Current(wall) || !PendingCurrent(wall, mono)) { pending = null; return Reject("relay-context-or-expiry"); }
            TrackWall(wall);
            if (decision == null || decision.PcId != PcId || decision.DeviceId != device.DeviceId || decision.Challenge != Convert.ToBase64String(pending!)) return Reject("relay-request-mismatch");
            var current = exchange.ReadCurrentChallenge();
            if (current == null || !current.AsSpan().SequenceEqual(pending)) { pending = null; return Reject("native-request-changed"); }
            byte[]? signature = null;
            if (decision.Approved)
            {
                try { signature = Convert.FromBase64String(decision.Signature); } catch (Exception error) when (error is FormatException or ArgumentException) { return Reject("relay-signature-invalid"); }
                if (signature.Length != 64) return Reject("relay-signature-invalid");
                var approval = new byte[WindowsGrantWire.ApprovalSize]; pending!.CopyTo(approval, 0); signature.CopyTo(approval, WindowsGrantWire.ChallengeSize);
                if (!WindowsGrantWire.Verify(WindowsGrantWire.ReadChallenge(pending), approval, nativeKey, WindowsGrantRelayWire.FileTimeMilliseconds(wall))) return Reject("relay-signature-invalid");
                Remember(decision.Challenge); var body = pending; pending = null;
                if (!Current(unixMilliseconds())) return Reject("relay-context-changed");
                bool submitted;
                try { submitted = exchange.Submit(body, approval); }
                catch (IOException) { Record(new(true, false, false, "native-exchange-io-error")); throw; }
                return Record(new(true, submitted, false, submitted ? "native-approval-submitted-awaiting-windows" : "native-exchange-unavailable"));
            }
            if (!Protocol.Verify(device.PublicKey, WindowsGrantRelayWire.DenialText(decision), decision.Signature)) return Reject("relay-denial-invalid");
            Remember(decision.Challenge); pending = null; return Reject("user-denied");
        }
    }
    private void Remember(string body)
    {
        consumed.Add(body); consumedOrder.Enqueue(body);
        // Still-valid requests may never be evicted. At this scale a native lock cycle is long expired.
        if (consumedOrder.Count > 4096) { revoked = true; pending = null; }
    }
    private WindowsGrantRelayResult Reject(string code) => Record(new(false, false, false, code));
    private WindowsGrantRelayResult Record(WindowsGrantRelayResult result)
    {
        decisions++; lastResult = result.Code;
        if (result.Submitted) submittedApprovals++;
        else if (result.Code == "user-denied") signedDenials++;
        else rejections++;
        return result; // Recording never changes the authentication result.
    }
}
