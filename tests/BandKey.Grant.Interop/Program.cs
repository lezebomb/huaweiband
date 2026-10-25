using BandKey.Core;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--verify-java")
{
    var target = args[1]; var expected = WindowsGrantWire.ReadChallenge(File.ReadAllBytes(Path.Combine(target, "native-challenge.bin")));
    var signed = File.ReadAllBytes(Path.Combine(target, "java-approval.bin")); var javaKey = File.ReadAllBytes(Path.Combine(target, "java-public.bin"));
    bool accepted = WindowsGrantWire.Verify(expected, signed, javaKey, 100001); signed[^1] ^= 1;
    bool refused = !WindowsGrantWire.Verify(expected, signed, javaKey, 100001);
    File.WriteAllText(Path.Combine(target, "csharp-java-result.json"), JsonSerializer.Serialize(new { passed = accepted && refused ? 2 : 0, failed = accepted && refused ? 0 : 1, realWindowsAuthenticationTested = false }));
    Console.WriteLine("CSharp verifies Java native grant: " + (accepted && refused)); return accepted && refused ? 0 : 1;
}
if(args.Length==2 && args[0]=="--verify-renewal-java")
{
    string dir=args[1];var proof=File.ReadAllBytes(Path.Combine(dir,"java-renewal.bin"));
    var request=WindowsRequestRenewalWire.Read(proof.AsSpan(0,436));var pub=File.ReadAllBytes(Path.Combine(dir,"java-public.bin"));
    bool good=WindowsRequestRenewalWire.Verify(request,proof,pub,100001);
    var forged=proof.ToArray();forged[^1]^=1;
    good &= !WindowsRequestRenewalWire.Verify(request,forged,pub,100001) &&
        !WindowsRequestRenewalWire.Verify(request,proof,pub,110000);
    try{WindowsGrantWire.ReadChallenge(proof.AsSpan(0,436));good=false;}catch(FormatException){}
    File.WriteAllText(Path.Combine(dir,"csharp-renewal-result.json"),JsonSerializer.Serialize(new{passed=good?4:0,failed=good?0:1,windowsAuthenticationSubmitted=false}));
    Console.WriteLine("CSharp verifies Java renewal purpose/signature/expiry: "+good);return good?0:1;
}
if (args.Length != 1) return 2;
var directory = args[0]; var tests = new List<object>(); var failed = 0;
void Check(string name, bool success) { tests.Add(new { name, passed = success }); if (!success) failed++; }
var body = File.ReadAllBytes(Path.Combine(directory, "native-challenge.bin"));
var native = File.ReadAllBytes(Path.Combine(directory, "native-approval.bin"));
var publicKey = File.ReadAllBytes(Path.Combine(directory, "native-public.bin"));
var challenge = WindowsGrantWire.ReadChallenge(body);
Check("Native challenge canonical round trip", body.SequenceEqual(WindowsGrantWire.WriteChallenge(challenge)));
Check("CSharp verifies CNG P1363 signature", WindowsGrantWire.Verify(challenge, native, publicKey, 100001));
var changed = native.ToArray(); changed[^1] ^= 1;
Check("CSharp refuses tampered native signature", !WindowsGrantWire.Verify(challenge, changed, publicKey, 100001));
Check("CSharp refuses wrong Windows session", !WindowsGrantWire.Verify(challenge with { WindowsSessionId = 6 }, native, publicKey, 100001));
Check("CSharp refuses wrong target SID", !WindowsGrantWire.Verify(challenge with { UserSid = "S-1-5-21-100-200-300-1002" }, native, publicKey, 100001));
Check("CSharp refuses expired signature", !WindowsGrantWire.Verify(challenge, native, publicKey, 130000));
Check("CSharp refuses future-issued challenge", !WindowsGrantWire.Verify(challenge, native, publicKey, 99999));
bool Refuses(byte[] value) { try { WindowsGrantWire.ReadChallenge(value); return false; } catch (FormatException) { return true; } }
Check("CSharp refuses truncated wire", Refuses(body[..^1]));
Check("CSharp refuses appended wire", Refuses(body.Concat(new byte[] { 0 }).ToArray()));
var malformed = body.ToArray(); malformed[^1] = 1;
Check("CSharp refuses noncanonical SID padding", Refuses(malformed));
malformed = body.ToArray(); malformed[0] ^= 1;
Check("CSharp refuses wrong wire version", Refuses(malformed));
bool RefusesChallenge(WindowsGrantChallenge value) { try { WindowsGrantWire.WriteChallenge(value); return false; } catch (FormatException) { return true; } }
Check("CSharp refuses newline in target SID", RefusesChallenge(challenge with { UserSid = challenge.UserSid + "\n" }));
Check("CSharp refuses overflowing SID component", RefusesChallenge(challenge with { UserSid = "S-1-5-21-4294967296-200-300-1001" }));
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var csharpPublic = WindowsGrantWire.ExportNativePublicKey(key);
var approval = WindowsGrantWire.Sign(challenge, key);
Check("CSharp signature self-check", WindowsGrantWire.Verify(challenge, approval, csharpPublic, 100001));
Check("Rotated public key rejected", !WindowsGrantWire.Verify(challenge, native, csharpPublic, 100001));
File.WriteAllBytes(Path.Combine(directory, "csharp-public.bin"), csharpPublic);
File.WriteAllBytes(Path.Combine(directory, "csharp-approval.bin"), approval);
File.WriteAllText(Path.Combine(directory, "csharp-result.json"), JsonSerializer.Serialize(new { total = tests.Count, passed = tests.Count - failed, failed, realWindowsAuthenticationTested = false, tests }));
Console.WriteLine($"Windows grant CSharp/CNG interop: {tests.Count - failed}/{tests.Count}; no Windows authentication submitted.");
return failed == 0 ? 0 : 1;
