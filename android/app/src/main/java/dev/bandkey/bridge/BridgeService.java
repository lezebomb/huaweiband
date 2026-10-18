package dev.bandkey.bridge;

import android.app.*;
import android.content.*;
import android.os.*;
import org.json.JSONObject;
import java.util.concurrent.*;

public final class BridgeService extends Service {
    private final ScheduledExecutorService worker=Executors.newSingleThreadScheduledExecutor();
    private static volatile BridgeService running;
    private volatile boolean stopped;
    private final PromptGate prompts=new PromptGate(()->System.currentTimeMillis()/1000,SystemClock::elapsedRealtime);
    private final RetrySchedule retries=new RetrySchedule();
    private BridgeClient client;
    static final String PREFS="bandkey";
    static void status(Context context,String value){context.getSharedPreferences(PREFS,MODE_PRIVATE).edit().putString("status",value).putLong("statusAt",System.currentTimeMillis()).apply();}
    static void stop(Context context){
        // apply() updates memory synchronously before scheduling disk persistence.
        context.getSharedPreferences(PREFS,MODE_PRIVATE).edit().putBoolean("enabled",false).apply();
        BridgeService service=running;
        if(service!=null)service.cancelWork();
        context.stopService(new Intent(context,BridgeService.class));
    }
    private void cancelWork(){stopped=true;prompts.stop();worker.shutdownNow();}
    private boolean enabled(){return !stopped && getSharedPreferences(PREFS,MODE_PRIVATE).getBoolean("enabled",false);}
    @Override public void onCreate(){
        super.onCreate();
        running=this;
        NotificationManager manager=getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("bandkey","BandKey 自动桥接",NotificationManager.IMPORTANCE_LOW));
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE);
        try {
            if(!enabled()){stopSelf();return;}
            startForeground(1,new android.app.Notification.Builder(this,"bandkey").setSmallIcon(android.R.drawable.ic_lock_lock)
                .setContentTitle("BandKey 桥接正在运行").setContentText("等待电脑请求和手环点击").setContentIntent(open).setOngoing(true).build());
            String config=getSharedPreferences(PREFS,MODE_PRIVATE).getString("pc",null);
            if(config==null)throw new IllegalStateException("尚未导入电脑配对资料");
            if(getSharedPreferences(PREFS,MODE_PRIVATE).getString("wearableUuid","").isEmpty())throw new IllegalStateException("尚未选择手环");
            client=new BridgeClient(new JSONObject(config),new Identity());
            worker.scheduleWithFixedDelay(this::poll,0,2,TimeUnit.SECONDS);
        }catch(Exception e){status(this,"启动失败："+e.getClass().getSimpleName());stop(this);}
    }
    private void poll(){
        if(!enabled() || !retries.ready(SystemClock.elapsedRealtime()))return;
        PowerManager.WakeLock wake=getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"BandKey:poll");
        try {
            wake.acquire(12000);
            JSONObject challenge=client.poll();retries.succeeded();
            if(challenge==null)return;
            if(!enabled())return;
            PromptGate.Ticket ticket=prompts.begin(challenge.getString("nonce"),challenge.getLong("expiresAt"));
            if(ticket==null)return;
            String uuid=getSharedPreferences(PREFS,MODE_PRIVATE).getString("wearableUuid","");
            new WearableChannel(this).prompt(uuid,"BandKey "+client.config.getString("pcId").substring(0,8),prompts,ticket,this::enabled,approved->{
                if(!enabled())return;
                try{worker.execute(()->{
                    if(!enabled() || !prompts.canSubmit(ticket))return;
                    try {
                        JSONObject result=client.approve(challenge,approved);
                        if(enabled())status(this,result!=null && result.optBoolean("authorized") ? "手环授权已验证；Windows 登录后端尚未可用" : "授权未通过/已拒绝");
                    }catch(Exception e){if(enabled())status(this,"反馈发送失败或已过期："+e.getClass().getSimpleName());}
                });}catch(RejectedExecutionException ignored){}
            },value->{if(enabled())status(this,value);});
        }catch(Exception e){
            retries.failed(SystemClock.elapsedRealtime());
            if(enabled())status(this,"连接等待重试："+e.getClass().getSimpleName());
        }finally{if(wake.isHeld())wake.release();}
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId){if(!enabled()){stopSelf();return START_NOT_STICKY;}return START_STICKY;}
    @Override public void onDestroy(){cancelWork();if(running==this)running=null;super.onDestroy();}
    @Override public IBinder onBind(Intent intent){return null;}
}
