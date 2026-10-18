package dev.bandkey.bridge;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.spec.ECGenParameterSpec;
import java.security.spec.X509EncodedKeySpec;
import org.json.JSONObject;

final class Identity {
    private final KeyStore store;
    private final String alias;
    Identity() throws Exception {this("bandkey-identity-v1");}
    Identity(String alias) throws Exception {
        this.alias=alias;
        store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (!store.containsAlias(alias)) {
            KeyPairGenerator gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore");
            gen.initialize(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN | KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1")).setDigests(KeyProperties.DIGEST_SHA256)
                .setUserAuthenticationRequired(false).build()); gen.generateKeyPair();
        }
    }
    String publicKey() throws Exception { return b64(store.getCertificate(alias).getPublicKey().getEncoded()); }
    byte[] nativePublicKey()throws Exception{return WindowsGrantWire.exportNative(store.getCertificate(alias).getPublicKey());}
    byte[] signNative(byte[] body)throws Exception{return WindowsGrantWire.sign(body,(PrivateKey)store.getKey(alias,null));}
    String deviceId() throws Exception { return hex(MessageDigest.getInstance("SHA-256").digest(Base64.decode(publicKey(), Base64.NO_WRAP))); }
    String sign(String text) throws Exception {
        return Wire.sign((PrivateKey)store.getKey(alias, null),text);
    }
    JSONObject export() throws Exception {
        return new JSONObject().put("deviceId",deviceId()).put("name", android.os.Build.MODEL + " · BandKey").put("publicKey",publicKey());
    }
    static boolean verify(String publicKey, String text, String signature) throws Exception {
        return Wire.verify(publicKey,text,signature);
    }
    static String b64(byte[] bytes) { return Base64.encodeToString(bytes, Base64.NO_WRAP); }
    static String hex(byte[] bytes) { StringBuilder s=new StringBuilder(); for(byte b:bytes)s.append(String.format(java.util.Locale.ROOT,"%02x",b & 255)); return s.toString(); }
    static String nonce() { byte[] bytes=new byte[32];new SecureRandom().nextBytes(bytes);return b64(bytes); }
    static String join(String... fields) {
        return Wire.join(fields);
    }
    static String challengeText(JSONObject c) throws Exception {
        return join("bandkey.challenge.v1",c.getString("pcId"),c.getString("deviceId"),c.getString("sessionId"),c.getString("nonce"),Long.toString(c.getLong("issuedAt")),Long.toString(c.getLong("expiresAt")));
    }
}
