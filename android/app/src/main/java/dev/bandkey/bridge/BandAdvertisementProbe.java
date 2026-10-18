package dev.bandkey.bridge;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.bluetooth.le.*;
import android.os.Handler;
import android.os.Looper;
import org.json.JSONArray;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.Locale;
import java.util.function.Consumer;

/** Bounded, foreground, address-filtered diagnostic. Never opens a connection. */
final class BandAdvertisementProbe {
    private final Activity activity;
    private final String address;
    private final Consumer<String> status;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final JSONObject report=new JSONObject();
    private final JSONArray samples=new JSONArray();
    private BluetoothLeScanner scanner;
    private boolean running;
    private final Runnable timeout=this::stop;
    BandAdvertisementProbe(Activity activity,String address,Consumer<String> status){
        this.activity=activity;this.address=address.toUpperCase(Locale.ROOT);this.status=status;
    }
    @SuppressLint("MissingPermission") // Activity checks BLUETOOTH_SCAN before starting.
    void start(){
        try{
            if(!BluetoothAdapter.checkBluetoothAddress(address))throw new IllegalArgumentException("MAC");
            report.put("address",address).put("startedAt",System.currentTimeMillis()).put("durationLimitSeconds",30)
                .put("neverForLocation",true).put("connectedOrPaired",false).put("samples",samples);
            BluetoothManager manager=activity.getSystemService(BluetoothManager.class);
            scanner=manager==null || manager.getAdapter()==null?null:manager.getAdapter().getBluetoothLeScanner();
            if(scanner==null)throw new IllegalStateException("Bluetooth unavailable");
            running=true;
            scanner.startScan(Collections.singletonList(new ScanFilter.Builder().setDeviceAddress(address).build()),
                new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),callback);
            save();handler.postDelayed(timeout,30000);status.accept("正在扫描指定手环广播，30 秒后自动停止");
        }catch(Exception e){failure(e.getClass().getSimpleName());}
    }
    private final ScanCallback callback=new ScanCallback(){
        @Override public void onScanResult(int type,ScanResult result){
            if(!running || samples.length()>=256)return;
            try{
                // Filter again defensively; do not persist neighbouring devices.
                if(!address.equalsIgnoreCase(result.getDevice().getAddress()))return;
                ScanRecord record=result.getScanRecord();
                JSONObject sample=new JSONObject().put("at",System.currentTimeMillis()).put("rssi",result.getRssi())
                    .put("connectable",result.isConnectable());
                if(record!=null){sample.put("name",record.getDeviceName()).put("rawHex",hex(record.getBytes()));}
                samples.put(sample);save();
            }catch(Exception e){failure(e.getClass().getSimpleName());}
        }
        @Override public void onScanFailed(int code){failure("Android scan error="+code);}
    };
    private void failure(String error){try{report.put("error",error);}catch(Exception ignored){}stop();save();status.accept("广播诊断失败："+error);}
    @SuppressLint("MissingPermission")
    void stop(){
        handler.removeCallbacks(timeout);
        if(!running)return;
        running=false;
        try{if(scanner!=null)scanner.stopScan(callback);}catch(Exception e){try{report.put("stopError",e.getClass().getSimpleName());}catch(Exception ignored){}}
        try{report.put("stoppedAt",System.currentTimeMillis());}catch(Exception ignored){}
        save();status.accept("广播诊断完成："+samples.length()+" 条目标广播；零条仅表示本次未观察到");
    }
    private synchronized void save(){
        try(java.io.FileOutputStream out=activity.openFileOutput("band-advertisements.json",Activity.MODE_PRIVATE)){
            out.write(report.toString(2).getBytes(StandardCharsets.UTF_8));
        }catch(Exception e){status.accept("无法保存广播诊断："+e.getClass().getSimpleName());}
    }
    private static String hex(byte[] bytes){StringBuilder out=new StringBuilder();for(byte b:bytes)out.append(String.format(Locale.ROOT,"%02x",b&255));return out.toString();}
}
