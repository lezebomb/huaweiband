package dev.bandkey.bridge;

import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.X509EncodedKeySpec;
import java.util.Base64;

/** Pure Java wire encoding, shared by the Android bridge and desktop/JDK interoperability test. */
public final class Wire {
    private Wire(){}
    public static String b64(byte[] bytes){return Base64.getEncoder().encodeToString(bytes);}
    public static String join(String... fields){
        for(String f:fields)if(f==null || f.isEmpty() || f.length()>4096 || f.contains("\n") || f.contains("\r"))throw new IllegalArgumentException("Invalid protocol field");
        return String.join("\n",fields);
    }
    public static String sign(PrivateKey key,String text)throws Exception{
        Signature sig=Signature.getInstance("SHA256withECDSA");sig.initSign(key);sig.update(text.getBytes(StandardCharsets.UTF_8));return b64(sig.sign());
    }
    public static boolean verify(String publicKey,String text,String signature)throws Exception{
        PublicKey key=KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(Base64.getDecoder().decode(publicKey)));
        Signature sig=Signature.getInstance("SHA256withECDSA");sig.initVerify(key);sig.update(text.getBytes(StandardCharsets.UTF_8));return sig.verify(Base64.getDecoder().decode(signature));
    }
    public static byte[] readBounded(java.io.InputStream input,int limit)throws java.io.IOException{
        java.io.ByteArrayOutputStream output=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[1024];int read;
        while((read=input.read(buffer))!=-1){if(output.size()+read>limit)throw new java.io.IOException("Oversized input");output.write(buffer,0,read);}
        return output.toByteArray();
    }
}
