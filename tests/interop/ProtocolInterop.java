import dev.bandkey.bridge.Wire;
import dev.bandkey.bridge.CertificatePinTrust;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.ECGenParameterSpec;
import java.util.*;

public final class ProtocolInterop {
    public static void main(String[] args)throws Exception {
        List<String> input=Files.readAllLines(Path.of(args[0]),StandardCharsets.UTF_8);
        String text=new String(Base64.getDecoder().decode(input.get(1)),StandardCharsets.UTF_8);
        if(!Wire.verify(input.get(0),text,input.get(2)))throw new SecurityException("Java rejected PC ECDSA signature");
        if(Wire.verify(input.get(0),text+"tampered",input.get(2)))throw new SecurityException("Java accepted tampering");
        java.security.cert.CertificateFactory factory=java.security.cert.CertificateFactory.getInstance("X.509");
        java.security.cert.X509Certificate certificate=(java.security.cert.X509Certificate)factory.generateCertificate(new java.io.ByteArrayInputStream(Base64.getDecoder().decode(input.get(3))));
        CertificatePinTrust trust=new CertificatePinTrust(input.get(4));
        trust.checkServerTrusted(new java.security.cert.X509Certificate[]{certificate},"ECDSA");
        try{new CertificatePinTrust("0".repeat(64)).checkServerTrusted(new java.security.cert.X509Certificate[]{certificate},"ECDSA");throw new SecurityException("Wrong TLS pin accepted");}catch(java.security.cert.CertificateException expected){}
        try{trust.checkServerTrusted(new java.security.cert.X509Certificate[0],"ECDSA");throw new SecurityException("Empty TLS chain accepted");}catch(java.security.cert.CertificateException expected){}
        java.security.cert.X509Certificate expired=(java.security.cert.X509Certificate)factory.generateCertificate(new java.io.ByteArrayInputStream(Base64.getDecoder().decode(input.get(5))));
        try{new CertificatePinTrust(input.get(6)).checkServerTrusted(new java.security.cert.X509Certificate[]{expired},"ECDSA");throw new SecurityException("Expired TLS peer accepted");}catch(java.security.cert.CertificateException expected){}
        KeyPairGenerator gen=KeyPairGenerator.getInstance("EC");gen.initialize(new ECGenParameterSpec("secp256r1"));KeyPair key=gen.generateKeyPair();
        Files.write(Path.of(args[1]),List.of(Wire.b64(key.getPublic().getEncoded()),Wire.sign(key.getPrivate(),text)),StandardCharsets.UTF_8);
        System.out.println("PC signature verified in Java; Java phone signature produced; correct/wrong/expired/empty TLS peer checks passed");
    }
}
