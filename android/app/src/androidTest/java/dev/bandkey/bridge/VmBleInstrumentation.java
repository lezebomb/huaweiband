package dev.bandkey.bridge;

import android.content.Context;
import android.os.SystemClock;
import java.io.*;
import java.nio.charset.StandardCharsets;
import org.json.*;

/** Prepared hardware test. USB carries setup/control only; the HTTPS stream must cross GATT. */
final class VmBleInstrumentation {
    private static void check(JSONArray checks,String name,boolean value)throws Exception{checks.put(new JSONObject().put("name",name).put("passed",value));if(!value)throw new SecurityException(name);}
    static JSONObject run(Context context,Identity lab,String runId,JSONArray checks)throws Exception{
        File dir=new File(context.getFilesDir(),"usb-test");JSONObject config;
        try(InputStream input=new FileInputStream(new File(dir,"pc.json"))){config=new JSONObject(new String(Wire.readBounded(input,8192),StandardCharsets.UTF_8));}
        check(checks,"BLE hardware test accepts only its loopback synthetic fixture",config.getString("endpoint").equals("https://127.0.0.1:19444") && config.optString("purpose").equals("bandkey-ble-instrumentation-v1") && runId.matches("[a-f0-9]{32}"));
        try(BlePhonePeripheral ble=new BlePhonePeripheral(context,19444,ignored->{})){
            ble.start();check(checks,"phone BLE advertisement started",ble.awaitAdvertising(10000));
            try(OutputStream output=new FileOutputStream(new File(dir,"ble-ready"))){output.write(runId.getBytes(StandardCharsets.UTF_8));}
            long deadline=SystemClock.elapsedRealtime()+60000;boolean go=false;
            while(SystemClock.elapsedRealtime()<deadline){File file=new File(dir,"ble-go");if(file.isFile()){String value;try(InputStream input=new FileInputStream(file)){value=new String(Wire.readBounded(input,128),StandardCharsets.UTF_8);}if(value.equals(runId)){go=true;break;}}Thread.sleep(200);}
            check(checks,"PC subscribed and controller supplied this run ID",go && ble.isConnected());
            VmRelayClient client=new VmRelayClient(config,lab);JSONObject prompt=client.poll();check(checks,"PC-signed prompt crossed BLE TLS",prompt!=null);
            WindowsGrantWire.Challenge body=WindowsGrantWire.read(android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP));
            check(checks,"only fixed synthetic account/session may auto-sign",body.sid.equals("S-1-5-21-111-222-333-1001") && body.session==5);
            JSONObject result=client.decide(prompt,true);check(checks,"BLE authorization submitted with no Windows unlock claim",result.getBoolean("authorized") && result.getBoolean("submitted") && !result.getBoolean("windowsUnlocked") && result.getBoolean("testOnly"));
            JSONObject replay=client.decide(prompt,true);check(checks,"BLE approval replay rejected",!replay.getBoolean("authorized") && !replay.getBoolean("submitted"));
            check(checks,"both GATT directions carried bytes",ble.sentBytes>0 && ble.receivedBytes>0 && ble.connections>=3);
            JSONObject wrong=new JSONObject(config.toString()).put("certificateSha256",new String(new char[64]).replace('\0','0'));boolean refused=false;
            try{new VmRelayClient(wrong,lab).poll();}catch(javax.net.ssl.SSLException error){refused=true;}check(checks,"wrong TLS certificate refused over BLE",refused);
            return new JSONObject().put("sentBytes",ble.sentBytes).put("receivedBytes",ble.receivedBytes).put("streams",ble.connections).put("usbForControlOnly",true);
        }
    }
}
