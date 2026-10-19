package dev.bandkey.bridge;
import java.nio.file.*;
import java.security.cert.CertificateException;
import javax.net.ssl.SSLException;
public final class TlsFailureClassifierTests {
    public static void main(String[] args)throws Exception{
        if(!TlsFailureClassifier.isPinMismatch(new SSLException(new CertificateException(new CertificateException("PC certificate pin mismatch")))))throw new AssertionError("Real pin failure not recognized");
        if(TlsFailureClassifier.isPinMismatch(new SSLException("BLE disconnected")))throw new AssertionError("Connection loss accepted as pin failure");
        if(TlsFailureClassifier.isPinMismatch(new CertificateException("expired")))throw new AssertionError("Wrong certificate reason");
        if(TlsFailureClassifier.isPinMismatch(new java.io.IOException("PC certificate pin mismatch")))throw new AssertionError("Non-certificate error");
        if(TlsFailureClassifier.isPinMismatch(null))throw new AssertionError("Null error");
        Files.writeString(Path.of(args[0]),"{\"passed\":5,\"failed\":0,\"physicalDeviceTested\":false}");
        System.out.println("TLS negative-test error classification: 5/5 passed.");
    }
}
