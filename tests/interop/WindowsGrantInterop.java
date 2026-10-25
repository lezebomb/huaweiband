import dev.bandkey.bridge.WindowsGrantWire;
import java.nio.file.*;
import java.security.*;
import java.security.spec.ECGenParameterSpec;
import java.util.*;

public final class WindowsGrantInterop {
    static int passed;
    static void check(boolean condition,String name){if(!condition)throw new IllegalStateException(name);passed++;}
    static boolean refuses(byte[] body){try{WindowsGrantWire.read(body);return false;}catch(IllegalArgumentException error){return true;}}
    public static void main(String[] args)throws Exception{
        Path dir=Path.of(args[0]);byte[] body=Files.readAllBytes(dir.resolve("native-challenge.bin"));
        byte[] nativeApproval=Files.readAllBytes(dir.resolve("native-approval.bin")),nativeKey=Files.readAllBytes(dir.resolve("native-public.bin"));
        PublicKey imported=WindowsGrantWire.importNative(nativeKey);
        check(Arrays.equals(WindowsGrantWire.exportNative(imported),nativeKey),"native key roundtrip");
        check(WindowsGrantWire.read(body).session==5,"native session parse");
        check(WindowsGrantWire.verify(body,Arrays.copyOfRange(nativeApproval,436,500),imported),"CNG signature in Java");
        byte[] bad=nativeApproval.clone();bad[499]^=1;check(!WindowsGrantWire.verify(body,Arrays.copyOfRange(bad,436,500),imported),"forged signature");
        check(refuses(Arrays.copyOf(body,435)),"truncated body");check(refuses(Arrays.copyOf(body,437)),"appended body");
        bad=body.clone();bad[0]^=1;check(refuses(bad),"wrong domain");bad=body.clone();bad[435]=1;check(refuses(bad),"SID padding");
        bad=body.clone();bad[16]=0;bad[17]=0;bad[18]=0;bad[19]=0;check(refuses(bad),"zero session");
        check(WindowsGrantWire.current(body,100001-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS),"current request");
        check(!WindowsGrantWire.current(body,130000-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS),"expired request");
        check(!WindowsGrantWire.current(body,99999-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS),"future request");
        byte[] csharp=Files.readAllBytes(dir.resolve("csharp-approval.bin"));PublicKey csharpKey=WindowsGrantWire.importNative(Files.readAllBytes(dir.resolve("csharp-public.bin")));
        check(WindowsGrantWire.verify(body,Arrays.copyOfRange(csharp,436,500),csharpKey),"CSharp signature in Java");
        KeyPairGenerator generator=KeyPairGenerator.getInstance("EC");generator.initialize(new ECGenParameterSpec("secp256r1"));KeyPair pair=generator.generateKeyPair();
        byte[] raw=WindowsGrantWire.sign(body,pair.getPrivate());check(raw.length==64 && WindowsGrantWire.verify(body,raw,pair.getPublic()),"Java P1363 signature");
        check(Arrays.equals(raw,WindowsGrantWire.derToP1363(WindowsGrantWire.p1363ToDer(raw))),"DER P1363 roundtrip");
        byte[] der=WindowsGrantWire.p1363ToDer(raw);byte[] trailing=Arrays.copyOf(der,der.length+1);boolean rejected=false;try{WindowsGrantWire.derToP1363(trailing);}catch(IllegalArgumentException error){rejected=true;}check(rejected,"DER trailing data");
        rejected=false;try{WindowsGrantWire.derToP1363(new byte[]{0x30,6,2,2,0,1,2,0});}catch(IllegalArgumentException error){rejected=true;}check(rejected,"DER redundant zero");
        rejected=false;try{WindowsGrantWire.derToP1363(new byte[]{0x30,6,2,1,(byte)0x80,2,1,1});}catch(IllegalArgumentException error){rejected=true;}check(rejected,"DER negative integer");
        rejected=false;try{WindowsGrantWire.derToP1363(Arrays.copyOf(der,der.length-1));}catch(IllegalArgumentException error){rejected=true;}check(rejected,"DER truncated integer");
        byte[] boundary=new byte[64];boundary[31]=1;boundary[32]=(byte)0x80;
        check(Arrays.equals(boundary,WindowsGrantWire.derToP1363(WindowsGrantWire.p1363ToDer(boundary))),"leading zero and high bit integers");
        check(Arrays.equals(new byte[64],WindowsGrantWire.derToP1363(WindowsGrantWire.p1363ToDer(new byte[64]))),"zero integer canonical encoding");
        check(!WindowsGrantWire.verify(body,new byte[64],pair.getPublic()),"zero r/s cannot verify");
        SecureRandom random=new SecureRandom();boolean roundtrips=true;
        for(int i=0;i<1000;i++){byte[] vector=new byte[64];random.nextBytes(vector);roundtrips&=Arrays.equals(vector,WindowsGrantWire.derToP1363(WindowsGrantWire.p1363ToDer(vector)));}
        check(roundtrips,"1000 P1363 integer roundtrips");
        bad=body.clone();byte[] overflow="S-1-5-21-4294967296-2-3-1001".getBytes(java.nio.charset.StandardCharsets.UTF_16LE);Arrays.fill(bad,68,bad.length,(byte)0);System.arraycopy(overflow,0,bad,68,overflow.length);check(refuses(bad),"SID uint overflow");
        byte[] invalidKey=nativeKey.clone();invalidKey[0]^=1;rejected=false;try{WindowsGrantWire.importNative(invalidKey);}catch(GeneralSecurityException error){rejected=true;}check(rejected,"wrong native key header");
        generator.initialize(new ECGenParameterSpec("secp384r1"));rejected=false;try{WindowsGrantWire.exportNative(generator.generateKeyPair().getPublic());}catch(GeneralSecurityException error){rejected=true;}check(rejected,"non P256 key");
        byte[] renewal=body.clone();
        System.arraycopy("BANDKEY-NEW-V1\0\0".getBytes(java.nio.charset.StandardCharsets.US_ASCII),0,renewal,0,16);
        java.nio.ByteBuffer.wrap(renewal).order(java.nio.ByteOrder.LITTLE_ENDIAN).putLong(28,110000L);
        check(WindowsGrantWire.readRenewal(renewal).expires==110000L,"renewal exact lifetime");
        check(WindowsGrantWire.currentRenewal(renewal,100001-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS) && !WindowsGrantWire.currentRenewal(renewal,110000-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS),"renewal expiry boundary");
        check(refuses(renewal),"request renewal is not an unlock challenge");
        boolean purposeRefused=false;try{WindowsGrantWire.sign(renewal,pair.getPrivate());}catch(IllegalArgumentException error){purposeRefused=true;}check(purposeRefused,"approval signer refuses renewal purpose");
        purposeRefused=false;try{WindowsGrantWire.signRenewal(body,pair.getPrivate());}catch(IllegalArgumentException error){purposeRefused=true;}check(purposeRefused,"renewal signer refuses unlock purpose");
        byte[] renewalSignature=WindowsGrantWire.signRenewal(renewal,pair.getPrivate());
        Signature renewalVerifier=Signature.getInstance("SHA256withECDSA");renewalVerifier.initVerify(pair.getPublic());renewalVerifier.update(renewal);
        check(renewalVerifier.verify(WindowsGrantWire.p1363ToDer(renewalSignature)),"production renewal signer");
        byte[] renewalProof=Arrays.copyOf(renewal,500);System.arraycopy(renewalSignature,0,renewalProof,436,64);
        Files.write(dir.resolve("java-renewal.bin"),renewalProof);
        byte[] response=Arrays.copyOf(body,500);System.arraycopy(raw,0,response,436,64);
        Files.write(dir.resolve("java-public.bin"),WindowsGrantWire.exportNative(pair.getPublic()));Files.write(dir.resolve("java-approval.bin"),response);
        Files.writeString(dir.resolve("java-result.json"),"{\"passed\":"+passed+",\"failed\":0,\"androidKeystoreTested\":false,\"windowsAuthenticationSubmitted\":false}");
        System.out.println("Android production grant codec JDK interop: "+passed+" checks passed; no device/Windows authentication.");
    }
}
