package dev.bandkey.bridge;

import java.security.KeyFactory;
import java.security.spec.X509EncodedKeySpec;
import org.json.JSONObject;

/** The VM-only manual test channel signs the same body as a future wearable confirmation adapter. */
final class VmRelayClient {
    final JSONObject config;
    final Identity identity;
    final BridgeClient transport;
    VmRelayClient(JSONObject config,Identity identity)throws Exception{
        if(!config.optBoolean("testOnly") || !"bandkey-vm-phone-v1".equals(config.optString("protocol")))throw new SecurityException("VM lab configuration required");
        this.config=config;this.identity=identity;transport=new BridgeClient(config,identity);
        WindowsGrantWire.exportNative(KeyFactory.getInstance("EC").generatePublic(new X509EncodedKeySpec(android.util.Base64.decode(config.getString("pcPublicKey"),android.util.Base64.NO_WRAP))));
    }
    JSONObject poll()throws Exception{
        JSONObject p=new JSONObject().put("pcId",config.getString("pcId")).put("deviceId",identity.deviceId()).put("nonce",Identity.nonce()).put("timestamp",System.currentTimeMillis()/1000);
        p.put("signature",identity.sign(Identity.join("bandkey.windows-grant.poll.v1",p.getString("pcId"),p.getString("deviceId"),p.getString("nonce"),Long.toString(p.getLong("timestamp")))));
        JSONObject prompt=transport.post("/v2/windows/poll",p);if(prompt!=null)validate(prompt);return prompt;
    }
    private byte[] validate(JSONObject prompt)throws Exception{
        if(!prompt.getString("pcId").equals(config.getString("pcId")) || !prompt.getString("deviceId").equals(identity.deviceId()) ||
            !Identity.verify(config.getString("pcPublicKey"),Identity.join("bandkey.windows-grant.prompt.v1",prompt.getString("pcId"),prompt.getString("deviceId"),prompt.getString("challenge")),prompt.getString("signature")))throw new SecurityException("PC prompt identity");
        byte[] body=android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP);
        if(!WindowsGrantWire.current(body,System.currentTimeMillis()))throw new SecurityException("Native request expired");return body;
    }
    JSONObject decide(JSONObject prompt,boolean approved)throws Exception{
        byte[] body=validate(prompt);
        String signature=approved?Identity.b64(identity.signNative(body)):identity.sign(Identity.join("bandkey.windows-grant.deny.v1",config.getString("pcId"),identity.deviceId(),prompt.getString("challenge")));
        return transport.post("/v2/windows/decision",new JSONObject().put("pcId",config.getString("pcId")).put("deviceId",identity.deviceId()).put("challenge",prompt.getString("challenge")).put("approved",approved).put("signature",signature));
    }
}
