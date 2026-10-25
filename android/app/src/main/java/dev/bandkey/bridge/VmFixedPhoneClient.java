package dev.bandkey.bridge;

import android.os.SystemClock;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

/** Only creates request-renewal signatures. Unlock approval remains in VmWearSession's real button callback. */
final class VmFixedPhoneClient {
    private final VmRelayClient relay;private final String bandHash;
    private JSONObject context;private String lastRenewalBody;private long sequence,lastAttempt=-10000,probeUntil,probeAt;
    private volatile long lastSuccess=-10000;
    private JSONObject offeredPrompt;private volatile int inlinePrompts;
    private volatile boolean ready,stopped;
    private volatile String status="等待固定手机状态通道";
    private volatile int reports,renewals,connectedReports,disconnectedReports,unknownReports;
    private volatile long maxReportGap;
    private volatile String nativeState="not-observed";
    org.json.JSONObject report()throws org.json.JSONException{
        return new JSONObject().put("connectedReports",connectedReports).put("disconnectedReports",disconnectedReports).put("unknownReports",unknownReports)
            .put("inlinePromptResponses",inlinePrompts).put("maxReportGapMilliseconds",maxReportGap).put("nativeState",nativeState).put("ready",ready());
    }
    VmFixedPhoneClient(VmRelayClient relay,String uuid)throws Exception{
        this.relay=relay;bandHash=Identity.hex(MessageDigest.getInstance("SHA-256").digest(uuid.getBytes(StandardCharsets.UTF_8)));
    }
    boolean ready(){long now=SystemClock.elapsedRealtime();return !stopped && ready && now>=lastSuccess && now-lastSuccess<9000;}
    String status(){return status;}int reports(){return reports;}int renewals(){return renewals;}
    void stop(){stopped=true;ready=false;offeredPrompt=null;}
    JSONObject takePrompt()throws Exception{
        JSONObject value=offeredPrompt;offeredPrompt=null;if(stopped || !ready() || value==null)return null;
        relay.validate(value);return value;
    }
    private void acceptPrompt(JSONObject response)throws Exception{
        Object value=response.opt("prompt");if(value instanceof JSONObject){
            JSONObject prompt=(JSONObject)value;relay.validate(prompt);offeredPrompt=prompt;inlinePrompts++;
        }
    }
    private String pc()throws Exception{return relay.config.getString("pcId");}
    private String device()throws Exception{return relay.identity.deviceId();}
    private JSONObject context()throws Exception{
        JSONObject poll=new JSONObject().put("pcId",pc()).put("deviceId",device()).put("nonce",Identity.nonce()).put("timestamp",System.currentTimeMillis()/1000);
        poll.put("signature",relay.identity.sign(Identity.join("bandkey.fixed-phone.context-poll.v1",pc(),device(),poll.getString("nonce"),Long.toString(poll.getLong("timestamp")))));
        JSONObject c=relay.transport.post("/v2/windows/fixed/context",poll,3000);
        long now=System.currentTimeMillis()/1000;
        if(c==null || !pc().equals(c.getString("pcId")) || !device().equals(c.getString("deviceId")) || c.getLong("expiresAt")<=now || c.getLong("expiresAt")>now+1800 ||
            c.getLong("nextSequence")<1 || c.getLong("nextSequence")>9007199254740991L ||
            android.util.Base64.decode(c.getString("contextNonce"),android.util.Base64.NO_WRAP).length!=32 ||
            !Identity.verify(relay.config.getString("pcPublicKey"),Identity.join("bandkey.fixed-phone.context.v1",pc(),device(),c.getString("contextNonce"),Long.toString(c.getLong("expiresAt")),Long.toString(c.getLong("nextSequence"))),c.getString("signature")))throw new SecurityException("Fixed phone PC context");
        sequence=c.getLong("nextSequence")-1;return c;
    }
    void pulse(WearableConnectionSource.Sample sample,boolean wantPrompt){
        long now=SystemClock.elapsedRealtime();if(stopped || now-lastAttempt<3000 && !(now>=probeAt && now<probeUntil))return;lastAttempt=now;ready=false;
        try{
            if(context==null || context.getLong("expiresAt")<=System.currentTimeMillis()/1000)context=context();
            if(stopped)return;
            JSONObject p=new JSONObject().put("pcId",pc()).put("deviceId",device()).put("contextNonce",context.getString("contextNonce")).put("sequence",++sequence)
                .put("observedAtMilliseconds",sample.observedAt).put("bandHash",bandHash).put("connected",sample.connected==null?JSONObject.NULL:sample.connected);
            p.put("signature",relay.identity.sign(Identity.join("bandkey.fixed-phone.state.v1",pc(),device(),context.getString("contextNonce"),Long.toString(sequence),Long.toString(sample.observedAt),bandHash,sample.connected==null?"unknown":sample.connected?"true":"false")));
            if(wantPrompt)p.put("poll",relay.signedPoll());
            JSONObject result=relay.transport.post("/v2/windows/fixed/state",p,3000);
            if(stopped)return;
            if(result==null || !result.optBoolean("accepted") || result.optBoolean("windowsUnlocked"))throw new SecurityException("Fixed phone state response");
            acceptPrompt(result);reports++;if(sample.connected==null)unknownReports++;else if(sample.connected)connectedReports++;else disconnectedReports++;
            if(lastSuccess>=0)maxReportGap=Math.max(maxReportGap,SystemClock.elapsedRealtime()-lastSuccess);
            nativeState=result.optString("nativeState","not-observed");ready=result.optBoolean("canPoll");
            Object pending=result.opt("renewal");
            if(pending instanceof JSONObject){
                JSONObject renewal=(JSONObject)pending;
                if(!pc().equals(renewal.getString("pcId")) || !device().equals(renewal.getString("deviceId")) || !context.getString("contextNonce").equals(renewal.getString("contextNonce")) ||
                    !Identity.verify(relay.config.getString("pcPublicKey"),Identity.join("bandkey.fixed-phone.renewal.v1",pc(),device(),context.getString("contextNonce"),renewal.getString("body")),renewal.getString("signature")))throw new SecurityException("Request renewal PC signature");
                byte[] body=android.util.Base64.decode(renewal.getString("body"),android.util.Base64.NO_WRAP);
                if(!WindowsGrantWire.currentRenewal(body,System.currentTimeMillis()))throw new SecurityException("Request renewal expired");
                if(stopped)return;
                byte[] signature=relay.identity.signRenewal(body);
                if(stopped)return;
                JSONObject proof=new JSONObject().put("pcId",pc()).put("deviceId",device()).put("contextNonce",context.getString("contextNonce")).put("body",renewal.getString("body")).put("signature",Identity.b64(signature));
                proof.put("poll",relay.signedPoll());
                result=relay.transport.post("/v2/windows/fixed/renew",proof,3000);
                if(stopped)return;
                if(result==null || !result.optBoolean("accepted") || result.optBoolean("windowsUnlocked"))throw new SecurityException("Request renewal response");
                acceptPrompt(result);if(!renewal.getString("body").equals(lastRenewalBody)){renewals++;lastRenewalBody=renewal.getString("body");}nativeState=result.optString("nativeState","not-observed");ready=result.optBoolean("canPoll");
            }
            if(ready){probeUntil=0;}else if(pending instanceof JSONObject){probeUntil=SystemClock.elapsedRealtime()+2000;}if(probeUntil>0)probeAt=SystemClock.elapsedRealtime()+250;
            lastSuccess=SystemClock.elapsedRealtime();status=sample.connected==null?"手环连接状态未知":sample.connected?(ready?"手环在连接范围内，等待新请求":"手环已连接，等待原生新请求"):"手环已断开，等待返回";
        }catch(Exception error){context=null;ready=false;offeredPrompt=null;status="固定手机通道："+error.getClass().getSimpleName();}
    }
}
