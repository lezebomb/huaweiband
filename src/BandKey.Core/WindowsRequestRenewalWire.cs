using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BandKey.Core;

/// <summary>Request generation only. Its domain is deliberately invalid as an unlock approval.</summary>
public static class WindowsRequestRenewalWire
{
    public const int Size = WindowsGrantWire.ChallengeSize, ProofSize = WindowsGrantWire.ApprovalSize;
    private static ReadOnlySpan<byte> Domain => "BANDKEY-NEW-V1\0\0"u8;
    public static WindowsGrantChallenge Read(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length!=Size || !bytes[..16].SequenceEqual(Domain))throw new FormatException("Request-renewal domain");
        ulong issued=BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]),expires=BinaryPrimitives.ReadUInt64LittleEndian(bytes[28..]);
        if(issued>ulong.MaxValue-30000 || expires<=issued || expires-issued!=10000)throw new FormatException("Request-renewal lifetime");
        // Reuse the strict SID, padding, session and nonce grammar, not the authentication domain.
        byte[] shape=bytes.ToArray();"BANDKEY-WIN-V1\0\0"u8.CopyTo(shape);
        BinaryPrimitives.WriteUInt64LittleEndian(shape.AsSpan(28),issued+30000);
        return WindowsGrantWire.ReadChallenge(shape) with {ExpiresFileTimeMilliseconds=expires};
    }
    public static byte[] Write(WindowsGrantChallenge value)
    {
        if(value.IssuedFileTimeMilliseconds>ulong.MaxValue-30000 || value.ExpiresFileTimeMilliseconds<=value.IssuedFileTimeMilliseconds || value.ExpiresFileTimeMilliseconds-value.IssuedFileTimeMilliseconds!=10000)throw new FormatException("Request-renewal lifetime");
        byte[] body=WindowsGrantWire.WriteChallenge(value with{ExpiresFileTimeMilliseconds=value.IssuedFileTimeMilliseconds+30000});
        Domain.CopyTo(body);BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(28),value.ExpiresFileTimeMilliseconds);return body;
    }
    public static bool IsCurrent(WindowsGrantChallenge value,ulong now)=>value.ExpiresFileTimeMilliseconds>value.IssuedFileTimeMilliseconds && value.ExpiresFileTimeMilliseconds-value.IssuedFileTimeMilliseconds==10000 && now>=value.IssuedFileTimeMilliseconds && now<value.ExpiresFileTimeMilliseconds;
    public static byte[] Sign(WindowsGrantChallenge value,ECDsa key)
    {
        _=WindowsGrantWire.ExportNativePublicKey(key);
        byte[] body=Write(value),proof=new byte[ProofSize];body.CopyTo(proof,0);
        key.SignData(body,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation).CopyTo(proof,Size);return proof;
    }
    public static bool Verify(WindowsGrantChallenge expected,ReadOnlySpan<byte> proof,ReadOnlySpan<byte> publicKey,ulong now)
    {
        try{
            if(proof.Length!=ProofSize || !IsCurrent(expected,now) || !proof[..Size].SequenceEqual(Write(expected)))return false;
            using var key=WindowsGrantWire.ImportNativePublicKey(publicKey);
            return key.VerifyData(proof[..Size],proof[Size..],HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }catch(Exception error)when(error is FormatException or ArgumentException or CryptographicException){return false;}
    }
}
