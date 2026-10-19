package dev.bandkey.bridge;

import android.app.Instrumentation;
import android.os.Bundle;
import android.security.keystore.KeyInfo;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import org.json.*;

/** Debug test APK only. Automatic approvals are limited to the fixed synthetic account/session. */
public final class VmUsbInstrumentation extends Instrumentation {
    private Bundle arguments;
    private final JSONArray checks=new JSONArray();
    private void check(String name,boolean value)throws Exception{checks.put(new JSONObject().put("name",name).put("passed",value));if(!value)throw new SecurityException(name);}
    @Override public void onCreate(Bundle args){super.onCreate(args);arguments=args;start();}
    private File output(String name){File dir=new File(getTargetContext().getFilesDir(),"usb-test");dir.mkdirs();return new File(dir,name);}
    private void write(String name,byte[] bytes)throws Exception{try(OutputStream stream=new FileOutputStream(output(name))){stream.write(bytes);}}
    private void synthetic(JSONObject prompt)throws Exception{
        byte[] body=android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP);
        WindowsGrantWire.Challenge challenge=WindowsGrantWire.read(body);
        check("only synthetic account/session can be automatically signed",challenge.sid.equals("S-1-5-21-111-222-333-1001") && challenge.session==5);
    }
    @Override public void onStart(){
        JSONObject report=new JSONObject();boolean passed=false;
        android.os.PowerManager.WakeLock wake=null;
        try{
            // ColorOS may suspend the test process behind its installer UI. Keep
            // this explicit, foreground test alive without changing battery policy.
            runOnMainSync(()->getTargetContext().startActivity(new android.content.Intent(getTargetContext(),MainActivity.class).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK).putExtra("bandkeyVmLab",true)));
            android.os.PowerManager power=(android.os.PowerManager)getTargetContext().getSystemService(android.content.Context.POWER_SERVICE);
            wake=power.newWakeLock(android.os.PowerManager.PARTIAL_WAKE_LOCK,"BandKey:UsbInstrumentation");wake.acquire(arguments.getString("mode","").equals("ble")?180000:90000);
            String mode=arguments.getString("mode","export");Identity normal=new Identity();String original=normal.deviceId();Identity lab=new Identity("bandkey-vm-lab-identity-v1");
            write("phone-public.bin",lab.nativePublicKey());check("phone lab key differs from normal bridge identity",!lab.deviceId().equals(original));
            KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);PrivateKey privateKey=(PrivateKey)store.getKey("bandkey-vm-lab-identity-v1",null);
            KeyInfo info=KeyFactory.getInstance(privateKey.getAlgorithm(),"AndroidKeyStore").getKeySpec(privateKey,KeyInfo.class);
            report.put("keyInsideSecureHardware",info.isInsideSecureHardware());
            if(mode.equals("ble")){report.put("ble",VmBleInstrumentation.run(getTargetContext(),lab,arguments.getString("runId",""),checks));}
            else if(!mode.equals("export")){
                String text;try(InputStream input=new FileInputStream(output("pc.json"))){text=new String(Wire.readBounded(input,8192),StandardCharsets.UTF_8);}
                JSONObject config=new JSONObject(text);
                check("test is restricted to the USB loopback synthetic fixture",config.getString("endpoint").equals("https://127.0.0.1:19444") && config.optString("purpose").equals("bandkey-usb-instrumentation-v1"));
                VmRelayClient client=new VmRelayClient(config,lab);
                if(mode.equals("wrong-pin")){
                    config.put("certificateSha256",new String(new char[64]).replace('\0','0'));boolean refused=false;try{new VmRelayClient(config,lab).poll();}catch(javax.net.ssl.SSLException error){refused=TlsFailureClassifier.isPinMismatch(error);}
                    check("wrong TLS pin fails on physical Android device",refused);
                }else if(mode.equals("link-down") || mode.equals("revoked")){
                    boolean refused=false;try{client.poll();}catch(IOException error){refused=true;}
                    check(mode.equals("link-down")?"missing USB reverse mapping prevents transport":"revoked PC context prevents polling",refused);
                }else{
                    JSONObject prompt=client.poll();check("physical phone receives PC-signed native challenge",prompt!=null);synthetic(prompt);
                    if(mode.equals("expired")){
                        Thread.sleep(31000);boolean refused=false;try{client.decide(prompt,true);}catch(SecurityException error){refused=true;}
                        check("phone refuses to sign expired native request",refused);
                    }else if(mode.equals("approve") || mode.equals("deny")){
                        JSONObject result=client.decide(prompt,mode.equals("approve"));
                        check("response never claims Windows unlocked",!result.getBoolean("windowsUnlocked") && result.getBoolean("testOnly"));
                        check(mode.equals("approve")?"Keystore signature is verified and delivered over USB HTTPS":"signed phone denial is accepted without proof delivery",mode.equals("approve")?result.getBoolean("authorized") && result.getBoolean("submitted"):!result.getBoolean("submitted") && result.getString("code").equals("user-denied"));
                        JSONObject replay=client.decide(prompt,true);check("replayed physical phone approval is rejected",!replay.getBoolean("authorized") && !replay.getBoolean("submitted"));
                    }else throw new IllegalArgumentException("Unknown test mode");
                }
            }
            check("normal bridge identity remains unchanged",normal.deviceId().equals(original));passed=true;
            report.put("mode",mode).put("normalIdentityPreserved",true);
        }catch(Exception error){try{report.put("errorType",error.getClass().getSimpleName());}catch(JSONException ignored){}}
        finally{if(wake!=null && wake.isHeld())wake.release();}
        try{report.put("runId",arguments.getString("runId","")).put("passed",passed).put("checks",checks).put("syntheticAccount",true).put("automaticTestConfirmation",true).put("windowsAuthenticationSubmitted",false).put("realWearableTested",false);write("result.json",report.toString(2).getBytes(StandardCharsets.UTF_8));}
        catch(Exception error){passed=false;}
        Bundle result=new Bundle();result.putString("bandkeyResult",passed?"passed":"failed");finish(passed?-1:0,result);
    }
}
