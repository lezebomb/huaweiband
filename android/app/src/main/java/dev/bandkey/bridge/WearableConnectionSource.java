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
    private int queries,responses,trueResults,falseResults,unknownResults,errors;private long queryStarted,lastQueryMs,maxQueryMs;
    WearableConnectionSource(Context context,String uuid){this.context=context;this.uuid=uuid;}
    void start(){handler.post(tick);}
    synchronized Sample sample(){
        long now=SystemClock.elapsedRealtime();
        return observedElapsed>=0 && now>=observedElapsed && now-observedElapsed<9000 ? new Sample(connected,observedWall):new Sample(null,System.currentTimeMillis());
    }
    synchronized boolean connected(){return Boolean.TRUE.equals(sample().connected);}
    synchronized void stop(){stopped=true;handler.removeCallbacks(tick);connected=null;observedElapsed=-1;}
    synchronized org.json.JSONObject report()throws org.json.JSONException{
        long now=SystemClock.elapsedRealtime();
        return new org.json.JSONObject().put("queries",queries).put("responses",responses).put("connectedResults",trueResults)
            .put("disconnectedResults",falseResults).put("unknownResults",unknownResults).put("errors",errors)
            .put("pending",pending).put("sampleAgeMilliseconds",observedElapsed<0?-1:Math.max(0,now-observedElapsed))
            .put("pendingMilliseconds",pending?Math.max(0,now-queryStarted):0).put("lastQueryMilliseconds",lastQueryMs).put("maxQueryMilliseconds",maxQueryMs);
    }
    private synchronized void result(Boolean value){
        if(stopped)return;boolean returned=Boolean.TRUE.equals(value) && !Boolean.TRUE.equals(connected);pending=false;responses++;connected=value;observedElapsed=SystemClock.elapsedRealtime();observedWall=System.currentTimeMillis();
        lastQueryMs=Math.max(0,observedElapsed-queryStarted);maxQueryMs=Math.max(maxQueryMs,lastQueryMs);
        if(value==null)unknownResults++;else if(value)trueResults++;else falseResults++;
        handler.removeCallbacks(tick);handler.postDelayed(tick,returned?1000:3000);
    }
    private synchronized void failure(){if(!stopped){errors++;result(null);}}
    private final Runnable tick=new Runnable(){@Override public void run(){
        synchronized(WearableConnectionSource.this){if(stopped)return;if(pending){handler.postDelayed(this,3000);return;}pending=true;queries++;queryStarted=SystemClock.elapsedRealtime();}
        try{
            HiWear.getDeviceClient(context).getCommonDevice().addOnSuccessListener(devices->{
                Boolean value=null;int matches=0;
                if(devices!=null)for(Device device:devices)if(device!=null && uuid.equals(device.getUuid())){matches++;value=device.isConnected();}
                result(matches==1?value:null);
            }).addOnFailureListener(error->failure());
        }catch(RuntimeException error){failure();}

    }};
}
