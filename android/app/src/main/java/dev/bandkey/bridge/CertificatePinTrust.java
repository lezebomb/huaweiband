package dev.bandkey.bridge;

import javax.net.ssl.X509TrustManager;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.*;

/** Explicit self-signed peer enrollment: exact leaf pin and validity, never a trust-all manager. */
public final class CertificatePinTrust implements X509TrustManager {
    private final String pin;
    public CertificatePinTrust(String pin){
        if(pin==null || !pin.matches("[a-f0-9]{64}"))throw new IllegalArgumentException("Invalid certificate pin");
        this.pin=pin;
    }
    @Override public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
    @Override public void checkClientTrusted(X509Certificate[] chain,String auth)throws CertificateException{throw new CertificateException("Client certificates unused");}
    @Override public void checkServerTrusted(X509Certificate[] chain,String auth)throws CertificateException{
        try {
            if(chain==null || chain.length==0)throw new CertificateException("No certificate");
            chain[0].checkValidity();
            byte[] digest=MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
            StringBuilder actual=new StringBuilder();for(byte b:digest)actual.append(String.format(java.util.Locale.ROOT,"%02x",b & 255));
            if(!MessageDigest.isEqual(pin.getBytes(StandardCharsets.US_ASCII),actual.toString().getBytes(StandardCharsets.US_ASCII)))throw new CertificateException("PC certificate pin mismatch");
        }catch(GeneralSecurityException e){throw new CertificateException(e);}
    }
}
