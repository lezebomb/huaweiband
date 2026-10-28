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
    private final HostnameVerifier verifier;
    private final boolean reuseConnection;
    private volatile HttpsURLConnection activeConnection;
    private volatile boolean closed;
    private long reuseStarted;
    private volatile long requests,completed,lastRequestMilliseconds,maxRequestMilliseconds,retirements;
    private volatile long failedRequests,rateLimitedResponses,unavailableResponses;
    private volatile int lastStatusCode;
    private volatile String lastFailureKind="none";
    long completedResponses(){return completed;}
    JSONObject metrics()throws org.json.JSONException{return new JSONObject().put("reuseEnabled",reuseConnection).put("requests",requests).put("completed",completed)
        .put("lastRequestMilliseconds",lastRequestMilliseconds).put("maxRequestMilliseconds",maxRequestMilliseconds).put("retirements",retirements)
        .put("failedRequests",failedRequests).put("lastStatusCode",lastStatusCode).put("rateLimitedResponses",rateLimitedResponses)
        .put("unavailableResponses",unavailableResponses).put("lastFailureKind",lastFailureKind);}
    void close(){closed=true;HttpsURLConnection active=activeConnection;if(active!=null)try{active.disconnect();}catch(RuntimeException ignored){}}
    void retireForReconnect(){reuseStarted=0;HttpsURLConnection active=activeConnection;if(active!=null)try{active.disconnect();}catch(RuntimeException ignored){}}
    BridgeClient(JSONObject config, Identity identity) throws Exception {this(config,identity,false);}
    BridgeClient(JSONObject config, Identity identity,boolean reuseConnection) throws Exception {
        this.config=config; this.identity=identity;this.reuseConnection=reuseConnection;
        URI endpoint=new URI(config.getString("endpoint"));
        if(!"https".equals(endpoint.getScheme()) || endpoint.getHost()==null || endpoint.getRawUserInfo()!=null || endpoint.getRawQuery()!=null || endpoint.getRawFragment()!=null || !(endpoint.getPath().isEmpty() || endpoint.getPath().equals("/")))
            throw new IllegalArgumentException("Only HTTPS origin accepted");
        if(reuseConnection && (!"127.0.0.1".equals(endpoint.getHost()) || endpoint.getPort()!=19443 || !config.optBoolean("testOnly")))throw new SecurityException("Serial VM BLE reuse required");
        final String host=endpoint.getHost();verifier=(name,session)->name.equalsIgnoreCase(host);
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
    JSONObject post(String path,JSONObject body)throws Exception {return post(path,body,0);}
    JSONObject post(String path,JSONObject body,int shortTimeout)throws Exception {
        if(closed)throw new IOException("VM transport stopped");
        long began=android.os.SystemClock.elapsedRealtime();requests++;
        lastStatusCode=0;lastFailureKind="none";
        if(reuseStarted==0)reuseStarted=began;
        // Each BLE byte stream is bounded to 45 seconds. Retire through an HTTP
        // Connection: close response after 20 seconds, well before that limit.
        boolean retire=reuseConnection && began-reuseStarted>=20000;
        boolean reusable=false;
        HttpsURLConnection connection=(HttpsURLConnection)new URL(config.getString("endpoint").replaceAll("/$","")+path).openConnection();
        connection.setSSLSocketFactory(sockets);
        // Exact leaf-certificate pin authenticates the origin; no CA/hostname fallback is allowed.
        String expectedHost=new URI(config.getString("endpoint")).getHost();
        connection.setHostnameVerifier(verifier);
        connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(5000);connection.setReadTimeout(5000);
        connection.setRequestMethod("POST");connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");
        if(config.optBoolean("singleConnectionTransport") || "127.0.0.1".equals(expectedHost)){connection.setRequestProperty("Connection",reuseConnection && !retire?"keep-alive":"close");connection.setReadTimeout(15000);connection.setConnectTimeout(15000);}
        if(shortTimeout!=0){if(shortTimeout<1000 || shortTimeout>15000)throw new IllegalArgumentException("HTTP timeout");connection.setConnectTimeout(shortTimeout);connection.setReadTimeout(shortTimeout);}
        byte[] bytes=body.toString().getBytes(StandardCharsets.UTF_8);connection.setFixedLengthStreamingMode(bytes.length);
        activeConnection=connection;
        if(closed){connection.disconnect();throw new IOException("VM transport stopped");}
        try {
            try(OutputStream output=connection.getOutputStream()){output.write(bytes);}
            int code=connection.getResponseCode();
            lastStatusCode=code;if(code==429)rateLimitedResponses++;if(code==503)unavailableResponses++;
            if(code==204){try(InputStream input=connection.getInputStream()){}reusable=true;completed++;return null;}
            if(code!=200 && !((path.equals("/v1/approval") || path.equals("/v2/windows/decision") || path.startsWith("/v2/windows/fixed/")) && code==403))throw new IOException("PC HTTP "+code);
            try(InputStream input=code==200?connection.getInputStream():connection.getErrorStream()) {
                if(input==null)throw new IOException("Empty PC response");
                byte[] response=Wire.readBounded(input,8192);
                JSONObject result=new JSONObject(new String(response,StandardCharsets.UTF_8));
                if(code==403 && result.optBoolean("authorized"))throw new SecurityException("Inconsistent denial response");
                reusable=true;completed++;return result;
            }
        }catch(Exception error){failedRequests++;lastFailureKind=error.getClass().getSimpleName();throw error;}finally{
            lastRequestMilliseconds=Math.max(0,android.os.SystemClock.elapsedRealtime()-began);maxRequestMilliseconds=Math.max(maxRequestMilliseconds,lastRequestMilliseconds);
            if(!reuseConnection || !reusable || retire || closed){connection.disconnect();reuseStarted=0;if(retire)retirements++;}
        }
    }
}
