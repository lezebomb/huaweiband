package dev.bandkey.bridge;

import android.app.Activity;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.device.Device;
import com.huawei.wearengine.client.ServiceConnectionListener;
import org.json.JSONArray;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.util.function.Consumer;

final class WearDiagnostics {
    private final Activity activity;
    private final Consumer<String> status;
    private final JSONObject report=new JSONObject();
    WearDiagnostics(Activity activity,Consumer<String> status){this.activity=activity;this.status=status;}
    void run(){
        try{
            report.put("sdkVersion","5.0.3.304").put("androidSdk",android.os.Build.VERSION.SDK_INT)
                .put("expectedModel","CDY-B59").put("recordedAt",System.currentTimeMillis()).put("setup",WearSetup.report(activity));
            save();
        }catch(Exception e){record("reportError",WearErrors.describe(e));}
        try{
            HiWear.getWearEngineClient(activity,new ServiceConnectionListener(){
                @Override public void onServiceConnect(){record("serviceConnected",true);}
                @Override public void onServiceDisconnect(){record("serviceConnected",false);}
            }).getServiceApiLevel().addOnSuccessListener(level->record("serviceApiLevel",level))
                .addOnFailureListener(e->record("serviceApiLevelError",WearErrors.describe(e)));
        }catch(Exception e){record("serviceApiLevelError",WearErrors.describe(e));}
        try{
            HiWear.getDeviceClient(activity).getCommonDevice().addOnSuccessListener(devices->{
                JSONArray list=new JSONArray();
                for(Device device:devices){
                    String name=device.getName(),model=device.getModel();
                    if(!((name!=null && name.contains("Band 11")) || "CDY-B59".equals(model)))continue;
                    try{list.put(new JSONObject().put("name",name).put("model",model).put("softwareVersion",device.getSoftwareVersion())
                        .put("connected",device.isConnected()).put("notifyCapability",device.getNotifyCapability())
                        .put("p2pCapability",device.getP2pCapability()).put("supportValue",Device.DEVICE_CAPABILITY_SUPPORT));}
                    catch(Exception ignored){}
                }
                record("commonDevices",list);
                status.accept("能力诊断完成，匹配手环数："+list.length()+"；详细结果可导出");
            }).addOnFailureListener(e->{record("commonDevicesError",WearErrors.describe(e));status.accept("设备能力诊断："+WearErrors.describe(e));});
        }catch(Exception e){record("commonDevicesError",WearErrors.describe(e));status.accept(WearErrors.describe(e));}
    }
    private synchronized void record(String key,Object value){try{report.put(key,value);save();}catch(Exception ignored){}}
    private synchronized void save(){
        try(java.io.FileOutputStream output=activity.openFileOutput("wear-diagnostics.json",Activity.MODE_PRIVATE)){
            output.write(report.toString(2).getBytes(StandardCharsets.UTF_8));
        }catch(Exception ignored){}
    }
}
