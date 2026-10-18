package dev.bandkey.bridge;

import javax.net.ssl.*;
import java.net.*;
import java.security.*;
import java.security.cert.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

final class BridgeClient {
    final JSONObject config;
    final Identity identity;
    private final SSLSocketFactory sockets;
    BridgeClient(JSONObject config, Identity identity) throws Exception {
        this.config=config; this.identity=identity;
        URI endpoint=new URI(config.getString("endpoint"));
        if(!"https".equals(endpoint.getScheme()) || endpoint.getHost()==null || endpoint.getRawUserInfo()!=null || endpoint.getRawQuery()!=null || endpoint.getRawFragment()!=null || !(endpoint.getPath().isEmpty() || endpoint.getPath().equals("/")))
            throw new IllegalArgumentException("Only HTTPS origin accepted");
        // Bind manually imported PC key to PC identifier, independently of network/TLS discovery.
        String fingerprint=Identity.hex(MessageDigest.getInstance("SHA-256").digest(android.util.Base64.decode(config.getString("pcPublicKey"),android.util.Base64.NO_WRAP)));
        if(!fingerprint.equals(config.getString("pcId")) || !config.getString("certificateSha256").matches("[a-f0-9]{64}"))throw new SecurityException("Invalid PC identity");
        X509TrustManager trust=new CertificatePinTrust(config.getString("certificateSha256"));
        SSLContext context=SSLContext.getInstance("TLS"); context.init(null,new TrustManager[]{trust},new SecureRandom());sockets=context.getSocketFactory();
    }
    JSONObject poll() throws Exception {
        JSONObject p=new JSONObject().put("pcId",config.getString("pcId")).put("deviceId",identity.deviceId()).put("nonce",Identity.nonce()).put("timestamp",System.currentTimeMillis()/1000);
        p.put("signature",identity.sign(Identity.join("bandkey.poll.v1",p.getString("pcId"),p.getString("deviceId"),p.getString("nonce"),Long.toString(p.getLong("timestamp")))));
        JSONObject c=post("/v1/poll",p);
        if(c==null)return null;
        long now=System.currentTimeMillis()/1000;
        if(!c.getString("pcId").equals(config.getString("pcId")) || !c.getString("deviceId").equals(identity.deviceId()) || c.getLong("issuedAt")>now+5 || c.getLong("expiresAt")<=now || c.getLong("expiresAt")-c.getLong("issuedAt")!=30 ||
            android.util.Base64.decode(c.getString("nonce"),android.util.Base64.NO_WRAP).length!=32 || !Identity.verify(config.getString("pcPublicKey"),Identity.challengeText(c),c.getString("signature")))throw new SecurityException("Invalid PC challenge");
        return c;
    }
    JSONObject approve(JSONObject c,boolean approved) throws Exception {
        long now=System.currentTimeMillis()/1000;
        if(c.getLong("expiresAt")<=now)throw new SecurityException("Wearable prompt expired");
        JSONObject a=new JSONObject().put("challenge",c).put("approved",approved).put("source","wearengine-button");
        String text=Identity.join("bandkey.approval.v1",Identity.b64(Identity.challengeText(c).getBytes(StandardCharsets.UTF_8)),c.getString("signature"),approved?"1":"0","wearengine-button");
        a.put("signature",identity.sign(text)); return post("/v1/approval",a);
    }
    private JSONObject post(String path,JSONObject body)throws Exception {
        HttpsURLConnection connection=(HttpsURLConnection)new URL(config.getString("endpoint").replaceAll("/$","")+path).openConnection();
        connection.setSSLSocketFactory(sockets);
        // Exact leaf-certificate pin authenticates the origin; no CA/hostname fallback is allowed.
        String expectedHost=new URI(config.getString("endpoint")).getHost();
        connection.setHostnameVerifier((host,session)->host.equalsIgnoreCase(expectedHost));
        connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(5000);connection.setReadTimeout(5000);
        connection.setRequestMethod("POST");connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");
        byte[] bytes=body.toString().getBytes(StandardCharsets.UTF_8);connection.setFixedLengthStreamingMode(bytes.length);
        try {
            try(OutputStream output=connection.getOutputStream()){output.write(bytes);}
            int code=connection.getResponseCode(); if(code==204)return null;
            if(code!=200 && !(path.equals("/v1/approval") && code==403))throw new IOException("PC HTTP "+code);
            try(InputStream input=code==200?connection.getInputStream():connection.getErrorStream()) {
                if(input==null)throw new IOException("Empty PC response");
                byte[] response=Wire.readBounded(input,8192);
                JSONObject result=new JSONObject(new String(response,StandardCharsets.UTF_8));
                if(code==403 && result.optBoolean("authorized"))throw new SecurityException("Inconsistent denial response");
                return result;
            }
        }finally{connection.disconnect();}
    }
}
