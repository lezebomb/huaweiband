package dev.bandkey.bridge;

import android.content.Context;
import android.os.*;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.device.Device;

/** Read-only selected-band connection cache. An absent device or failed query is unknown, not away. */
final class WearableConnectionSource {
    static final class Sample {final Boolean connected;final long observedAt;Sample(Boolean connected,long observedAt){this.connected=connected;this.observedAt=observedAt;}}
    private final Context context;private final String uuid;private final Handler handler=new Handler(Looper.getMainLooper());
    private boolean stopped,pending;private Boolean connected;private long observedElapsed=-1,observedWall;
    WearableConnectionSource(Context context,String uuid){this.context=context;this.uuid=uuid;}
    void start(){handler.post(tick);}
    synchronized Sample sample(){
        long now=SystemClock.elapsedRealtime();
        return observedElapsed>=0 && now>=observedElapsed && now-observedElapsed<9000 ? new Sample(connected,observedWall):new Sample(null,System.currentTimeMillis());
    }
    synchronized boolean connected(){return Boolean.TRUE.equals(sample().connected);}
    synchronized void stop(){stopped=true;handler.removeCallbacks(tick);connected=null;observedElapsed=-1;}
    private synchronized void result(Boolean value){if(stopped)return;pending=false;connected=value;observedElapsed=SystemClock.elapsedRealtime();observedWall=System.currentTimeMillis();}
    private final Runnable tick=new Runnable(){@Override public void run(){
        synchronized(WearableConnectionSource.this){if(stopped)return;if(pending){handler.postDelayed(this,3000);return;}pending=true;}
        try{
            HiWear.getDeviceClient(context).getCommonDevice().addOnSuccessListener(devices->{
                Boolean value=null;int matches=0;
                for(Device device:devices)if(uuid.equals(device.getUuid())){matches++;value=device.isConnected();}
                result(matches==1?value:null);
            }).addOnFailureListener(error->result(null));
        }catch(RuntimeException error){result(null);}
        handler.postDelayed(this,3000);
    }};
}
