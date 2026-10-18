using System.Security.Cryptography;
using System.Text;

namespace BandKey.Core;

public sealed record PublicDevice(string DeviceId, string Name, string PublicKey);
public sealed record Challenge(string PcId, string DeviceId, string SessionId, string Nonce,
    long IssuedAt, long ExpiresAt, string Signature);
public sealed record PollRequest(string PcId, string DeviceId, string Nonce, long Timestamp, string Signature);
public sealed record Approval(Challenge Challenge, bool Approved, string Source, string Signature);
public sealed record ApprovalResult(bool Authorized, bool WindowsUnlocked, string Code);

public static class Protocol
{
    public static string Fingerprint(string publicKey) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)));
    public static string ExportPublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static ECDsa ReadPublicKey(string publicKey)
    {
        var bytes = Convert.FromBase64String(publicKey);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Only ECDSA P-256 SPKI is accepted");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
    public static string Nonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string ChallengeText(Challenge c) => Join("bandkey.challenge.v1", c.PcId, c.DeviceId, c.SessionId, c.Nonce, c.IssuedAt.ToString(System.Globalization.CultureInfo.InvariantCulture), c.ExpiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string PollText(PollRequest p) => Join("bandkey.poll.v1", p.PcId, p.DeviceId, p.Nonce, p.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string ApprovalText(Approval a) => Join("bandkey.approval.v1", Convert.ToBase64String(Encoding.UTF8.GetBytes(ChallengeText(a.Challenge))), a.Challenge.Signature, a.Approved ? "1" : "0", a.Source);
    public static string Sign(ECDsa key, string text) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    public static bool Verify(string publicKey, string text, string signature)
    {
        try
        {
            using var key = ReadPublicKey(publicKey);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static bool ValidNonce(string? nonce)
    {
        try { return nonce != null && nonce.Length == 44 && Convert.FromBase64String(nonce).Length == 32; }
        catch (FormatException) { return false; }
    }
    private static string Join(params string[] values)
    {
        if (values.Any(v => string.IsNullOrEmpty(v) || v.Length > 4096 || v.Contains('\n') || v.Contains('\r')))
            throw new ArgumentException("Invalid protocol field");
        return string.Join('\n', values);
    }
}

/// <summary>In-memory, bounded, thread-safe challenge broker. Restart invalidates every pending approval.</summary>
public sealed class AuthorizationBroker
{
    private readonly object gate = new();
    private readonly Dictionary<string, PublicDevice> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Challenge> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pollNonces = new(StringComparer.Ordinal);
    private readonly ECDsa pcKey;
    private readonly string pcId;
    private string? lockSession;
    private bool locked, near;
    private long lastChallengeAt = long.MinValue;
    public string PcPublicKey { get; }

    public AuthorizationBroker(string pcId, ECDsa pcKey)
    { this.pcId = pcId; this.pcKey = pcKey; PcPublicKey = Protocol.ExportPublicKey(pcKey); }

    public void Enroll(PublicDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        using var key = Protocol.ReadPublicKey(device.PublicKey);
        if (device.DeviceId != Protocol.Fingerprint(device.PublicKey) || string.IsNullOrWhiteSpace(device.Name) || device.Name.Length > 100)
            throw new ArgumentException("Device ID must equal SHA256(SPKI)");
        lock (gate)
        {
            if (devices.Count >= 64 && !devices.ContainsKey(device.DeviceId)) throw new ArgumentException("Enrollment capacity reached");
            devices[device.DeviceId] = device; pending.Remove(device.DeviceId);
        }
    }
    public void Revoke(string deviceId)
    { lock (gate) { devices.Remove(deviceId); pending.Remove(deviceId); } }
    public PublicDevice[] Devices { get { lock (gate) return devices.Values.ToArray(); } }

    public void SetContext(bool isLocked, bool isNear)
    {
        lock (gate)
        {
            if (isLocked != locked)
            {
                pending.Clear(); lockSession = isLocked ? Guid.NewGuid().ToString("N") : null;
                lastChallengeAt = long.MinValue;
            }
            locked = isLocked; near = isNear;
            if (!near) pending.Clear();
        }
    }

    public Challenge? Poll(PollRequest poll, long now)
    {
        lock (gate)
        {
            if (poll == null || poll.DeviceId == null || poll.Signature == null) throw new UnauthorizedAccessException("poll-malformed");
            foreach (var k in pollNonces.Where(x => x.Value < now - 60).Select(x => x.Key).ToArray()) pollNonces.Remove(k);
            if (poll.PcId != pcId || !devices.TryGetValue(poll.DeviceId, out var device) ||
                poll.Timestamp < now - 30 || poll.Timestamp > now + 30 || !Protocol.ValidNonce(poll.Nonce) ||
                !Protocol.Verify(device.PublicKey, Protocol.PollText(poll), poll.Signature))
                throw new UnauthorizedAccessException("poll-invalid");
            var nonceKey = poll.DeviceId + ":" + poll.Nonce;
            if (pollNonces.Count >= 4096 || !pollNonces.TryAdd(nonceKey, now)) throw new UnauthorizedAccessException("poll-replay-or-capacity");
            if (!locked || !near || lockSession == null) return null;
            if (pending.TryGetValue(poll.DeviceId, out var existing) && existing.ExpiresAt > now) return existing;
            // No repeated wearable prompts every poll: at most one new challenge per 60s globally.
            if (lastChallengeAt != long.MinValue && now - lastChallengeAt < 60) return null;
            var challenge = new Challenge(pcId, poll.DeviceId, lockSession, Protocol.Nonce(), now, now + 30, "");
            challenge = challenge with { Signature = Protocol.Sign(pcKey, Protocol.ChallengeText(challenge)) };
            pending[poll.DeviceId] = challenge; lastChallengeAt = now; return challenge;
        }
    }

    public ApprovalResult Accept(Approval approval, long now)
    {
        lock (gate)
        {
            var c = approval.Challenge;
            if (c == null || c.DeviceId == null) return Reject("approval-malformed");
            if (!locked || !near || c.SessionId != lockSession) return Reject("context-invalid");
            if (!devices.TryGetValue(c.DeviceId, out var device)) return Reject("device-revoked");
            if (!pending.TryGetValue(c.DeviceId, out var expected) || expected != c) return Reject("challenge-unknown-or-consumed");
            if (now < c.IssuedAt || now >= c.ExpiresAt) { pending.Remove(c.DeviceId); return Reject("challenge-expired"); }
            if (approval.Source != "wearengine-button") return Reject("button-source-required");
            if (!Protocol.Verify(device.PublicKey, Protocol.ApprovalText(approval), approval.Signature)) return Reject("signature-invalid");
            pending.Remove(c.DeviceId); // atomically consume signed approvals AND denials
            return approval.Approved ? new(true, false, "authorized-authentication-backend-unavailable") : Reject("user-denied");
        }
    }
    private static ApprovalResult Reject(string code) => new(false, false, code);
}
