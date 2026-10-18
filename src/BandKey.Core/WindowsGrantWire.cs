using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BandKey.Core;

// Independent from the existing phone approval protocol. Production agent integration
// must explicitly request this signed, account/session-bound payload on the device.
public sealed record WindowsGrantChallenge(uint WindowsSessionId, ulong IssuedFileTimeMilliseconds,
    ulong ExpiresFileTimeMilliseconds, string UserSid, string NonceBase64);

public static class WindowsGrantWire
{
    public const int ChallengeSize = 436, ApprovalSize = 500;
    private static ReadOnlySpan<byte> Domain => "BANDKEY-WIN-V1\0\0"u8;
    private static readonly Regex LocalSid = new(@"\AS-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+\z", RegexOptions.CultureInvariant);

    public static WindowsGrantChallenge ReadChallenge(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ChallengeSize || !bytes[..16].SequenceEqual(Domain)) throw new FormatException("Windows grant format");
        var sidBytes = bytes[68..];
        var end = -1;
        for (var i = 0; i < sidBytes.Length; i += 2) if (sidBytes[i] == 0 && sidBytes[i + 1] == 0) { end = i; break; }
        if (end <= 0 || sidBytes[end..].ContainsAnyExcept((byte)0)) throw new FormatException("Windows grant SID padding");
        var value = new WindowsGrantChallenge(BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[28..]),
            Encoding.Unicode.GetString(sidBytes[..end]), Convert.ToBase64String(bytes.Slice(36, 32)));
        Validate(value); return value;
    }
    public static byte[] WriteChallenge(WindowsGrantChallenge value)
    {
        Validate(value); var bytes = new byte[ChallengeSize]; Domain.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), value.WindowsSessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), value.IssuedFileTimeMilliseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), value.ExpiresFileTimeMilliseconds);
        Convert.FromBase64String(value.NonceBase64).CopyTo(bytes, 36);
        Encoding.Unicode.GetBytes(value.UserSid).CopyTo(bytes, 68); return bytes;
    }
    public static bool IsCurrent(WindowsGrantChallenge value, ulong now) =>
        value.ExpiresFileTimeMilliseconds > value.IssuedFileTimeMilliseconds && value.ExpiresFileTimeMilliseconds - value.IssuedFileTimeMilliseconds == 30000 && now >= value.IssuedFileTimeMilliseconds && now < value.ExpiresFileTimeMilliseconds;

    public static byte[] Sign(WindowsGrantChallenge value, ECDsa key)
    {
        _ = ExportNativePublicKey(key); // Reject other curves before signing.
        var body = WriteChallenge(value); var approval = new byte[ApprovalSize]; body.CopyTo(approval, 0);
        key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).CopyTo(approval, ChallengeSize);
        return approval;
    }
    public static bool Verify(WindowsGrantChallenge expected, ReadOnlySpan<byte> approval, ReadOnlySpan<byte> nativePublicKey, ulong now)
    {
        try
        {
            if (approval.Length != ApprovalSize || !IsCurrent(expected, now) || !approval[..ChallengeSize].SequenceEqual(WriteChallenge(expected))) return false;
            using var key = ReadNativePublicKey(nativePublicKey);
            return key.VerifyData(approval[..ChallengeSize], approval[ChallengeSize..], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static byte[] ExportNativePublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        if (key.KeySize != 256 || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" || parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32)
            throw new CryptographicException("Only P-256 is supported");
        var bytes = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x31534345); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 32);
        parameters.Q.X.CopyTo(bytes, 8); parameters.Q.Y.CopyTo(bytes, 40); return bytes;
    }
    private static ECDsa ReadNativePublicKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 72 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31534345 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 32) throw new FormatException("Native P-256 key blob");
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = bytes.Slice(8, 32).ToArray(), Y = bytes.Slice(40, 32).ToArray() } });
    }
    private static void Validate(WindowsGrantChallenge value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.WindowsSessionId != 0 && value.UserSid != null && value.UserSid.Length < 184 && LocalSid.IsMatch(value.UserSid) &&
            value.UserSid.Split('-').Skip(4).All(x => uint.TryParse(x, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) && Protocol.ValidNonce(value.NonceBase64) &&
            value.ExpiresFileTimeMilliseconds > value.IssuedFileTimeMilliseconds && value.ExpiresFileTimeMilliseconds - value.IssuedFileTimeMilliseconds == 30000) return;
        throw new FormatException("Windows grant account, session, nonce or lifetime");
    }
}
