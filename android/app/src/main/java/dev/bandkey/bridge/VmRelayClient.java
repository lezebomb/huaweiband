package dev.bandkey.bridge;

import java.security.KeyFactory;
import java.security.spec.X509EncodedKeySpec;
import org.json.JSONObject;

/** The VM-only manual test channel signs the same body as a future wearable confirmation adapter. */
final class VmRelayClient {
    final JSONObject config;
    final Identity identity;
    final BridgeClient transport;
    final boolean hostPilot;
    private final boolean realWearableClient;
    VmRelayClient(JSONObject config,Identity identity)throws Exception{this(config,identity,false);}
    VmRelayClient(JSONObject config,Identity identity,boolean reuseConnection)throws Exception{
        this(config,identity,reuseConnection,false);
    }
    VmRelayClient(JSONObject config,Identity identity,boolean reuseConnection,boolean realWearableClient)throws Exception{
        if(!HostConfirmationPolicy.validConfiguration(config.optString("protocol"),config.optBoolean("testOnly"),config.optBoolean("realWearableOnly")))throw new SecurityException("Unsupported confirmation profile");
        hostPilot=HostConfirmationPolicy.isHost(config.optString("protocol"));this.realWearableClient=realWearableClient;
        this.config=config;this.identity=identity;transport=new BridgeClient(config,identity,reuseConnection);
        WindowsGrantWire.exportNative(KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(android.util.Base64.decode(config.getString("pcPublicKey"),android.util.Base64.NO_WRAP))));
    }
    JSONObject signedPoll()throws Exception{
        JSONObject p=new JSONObject().put("pcId",config.getString("pcId")).put("deviceId",identity.deviceId()).put("nonce",Identity.nonce()).put("timestamp",System.currentTimeMillis()/1000);
        p.put("signature",identity.sign(Identity.join("bandkey.windows-grant.poll.v1",p.getString("pcId"),p.getString("deviceId"),p.getString("nonce"),Long.toString(p.getLong("timestamp")))));
        return p;
    }
    JSONObject poll()throws Exception{return poll(0);}
    JSONObject poll(int timeout)throws Exception{
        JSONObject prompt=transport.post("/v2/windows/poll",signedPoll(),timeout);if(prompt!=null)validate(prompt);return prompt;
    }
    byte[] validate(JSONObject prompt)throws Exception{
        if(!prompt.getString("pcId").equals(config.getString("pcId")) || !prompt.getString("deviceId").equals(identity.deviceId()) ||
            !Identity.verify(config.getString("pcPublicKey"),Identity.join("bandkey.windows-grant.prompt.v1",prompt.getString("pcId"),prompt.getString("deviceId"),prompt.getString("challenge")),prompt.getString("signature")))throw new SecurityException("PC prompt identity");
        byte[] body=android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP);
        if(!WindowsGrantWire.current(body,System.currentTimeMillis()))throw new SecurityException("Native request expired");return body;
    }
    JSONObject decide(JSONObject prompt,boolean approved)throws Exception{return decide(prompt,approved,0);}
    JSONObject decide(JSONObject prompt,boolean approved,int timeout)throws Exception{
        if(!HostConfirmationPolicy.mayDecide(hostPilot,realWearableClient))throw new SecurityException("Host confirmation requires the real-band service");
        byte[] body=validate(prompt);
        String signature=approved?Identity.b64(identity.signNative(body)):identity.sign(Identity.join("bandkey.windows-grant.deny.v1",config.getString("pcId"),identity.deviceId(),prompt.getString("challenge")));
        return transport.post("/v2/windows/decision",new JSONObject().put("pcId",config.getString("pcId")).put("deviceId",identity.deviceId()).put("challenge",prompt.getString("challenge")).put("approved",approved).put("signature",signature),timeout);
    }
}
