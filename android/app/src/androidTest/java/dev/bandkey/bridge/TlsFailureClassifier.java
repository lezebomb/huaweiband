package dev.bandkey.bridge;

/** Do not mistake a dropped BLE connection for rejection by the certificate pin. */
final class TlsFailureClassifier {
    static boolean isPinMismatch(Throwable error){
        for(int depth=0;error!=null && depth<16;depth++,error=error.getCause())
            if(error instanceof java.security.cert.CertificateException && "PC certificate pin mismatch".equals(error.getMessage()))return true;
        return false;
    }
}
