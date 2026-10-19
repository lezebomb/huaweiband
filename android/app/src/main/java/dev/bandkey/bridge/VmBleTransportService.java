package dev.bandkey.bridge;

import android.Manifest;
import android.app.*;
import android.bluetooth.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.net.Uri;
import android.os.*;
import java.util.UUID;
import org.json.*;

/** Optional VM lab transport only. It never polls, prompts, signs, or receives credentials. */
public final class VmBleTransportService extends Service {
    private static final String START="dev.bandkey.bridge.VM_BLE_START",STOP="dev.bandkey.bridge.VM_BLE_STOP",CHANNEL="bandkey-vm-ble";
    private static final int NOTIFICATION=41;
    private static volatile VmBleTransportService running;
    private static volatile BleBackgroundLease.Snapshot last;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final BleBackgroundLease lease=new BleBackgroundLease();
    private String token,notificationState="";
    private boolean finishing;
    private final Runnable timer=new Runnable(){@Override public void run(){
        lease.tick(SystemClock.elapsedRealtime(),bluetoothAllowed());last=lease.snapshot();
        if(last.stage==BleBackgroundLease.Stage.STOPPED){finish(last.reason);return;}
        updateNotification();handler.postDelayed(this,1000);
    }};

    static boolean active(){VmBleTransportService service=running;return service!=null && service.lease.allows(SystemClock.elapsedRealtime());}
    static boolean requestStart(Activity activity){
        if(active())return true;
        if(activity.isFinishing() || !activity.hasWindowFocus() || !activity.getSystemService(PowerManager.class).isInteractive() || activity.getSystemService(KeyguardManager.class).isKeyguardLocked())return false;
        activity.startForegroundService(new Intent(activity,VmBleTransportService.class).setAction(START).putExtra("run",UUID.randomUUID().toString()));return true;
    }
    static void stopCurrent(){VmBleTransportService service=running;if(service!=null)service.finish(BleBackgroundLease.Reason.USER);}
    static String summary(){
        BleBackgroundLease.Snapshot s=last;if(s==null)return "后台 BLE 未启动。";
        return "后台 BLE："+s.stage+" / "+s.reason+"\n剩余 "+((s.remaining+999)/1000)+" 秒；流 "+s.streams+"；断连 "+s.disconnects+"；发送/接收 "+s.sent+"/"+s.received+" 字节。\n仅传输；退到后台后本页不轮询、不签署确认。";
    }
    static String report()throws JSONException{
        BleBackgroundLease.Snapshot s=last;if(s==null)throw new IllegalStateException("No BLE transport run");
        JSONArray events=new JSONArray();for(BleBackgroundLease.Event event:s.events)events.put(new JSONObject().put("elapsedMilliseconds",event.elapsed).put("stage",event.stage.name()).put("reason",event.reason.name()));
        return new JSONObject().put("schema","bandkey.ble-background.v1").put("stage",s.stage.name()).put("reason",s.reason.name())
            .put("elapsedMilliseconds",s.elapsed).put("remainingMilliseconds",s.remaining).put("sentBytes",s.sent).put("receivedBytes",s.received).put("streams",s.streams).put("disconnects",s.disconnects)
            .put("eventsTruncated",s.truncated).put("cleanupFailed",s.cleanupFailed).put("events",events).put("scope","phone-transport-only")
            .put("independentlyValidated",false).put("applicationResponseVerified",false).put("windowsAuthenticationSubmitted",false).put("realWearableTested",false).toString(2);
    }
    private boolean bluetoothAllowed(){
        try{
            if(Build.VERSION.SDK_INT>=31 && (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)!=PackageManager.PERMISSION_GRANTED || checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE)!=PackageManager.PERMISSION_GRANTED))return false;
            BluetoothManager manager=getSystemService(BluetoothManager.class);BluetoothAdapter adapter=manager==null?null:manager.getAdapter();return adapter!=null && adapter.isEnabled();
        }catch(RuntimeException error){return false;}
    }
    @Override public void onCreate(){super.onCreate();running=this;}
    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent!=null && STOP.equals(intent.getAction())){
            if(token!=null && token.equals(intent.getStringExtra("run")))finish(BleBackgroundLease.Reason.USER);
            else if(token==null)stopSelf();
            return START_NOT_STICKY;
        }
        if(token!=null)return START_NOT_STICKY;
        String requested=intent==null?null:intent.getStringExtra("run");
        if(intent==null || !START.equals(intent.getAction()) || requested==null || !requested.matches("[a-f0-9-]{36}")){
            stopSelf();return START_NOT_STICKY;
        }
        token=requested;
        if(!bluetoothAllowed() || !getSystemService(NotificationManager.class).areNotificationsEnabled()){finish(BleBackgroundLease.Reason.START_FAILED);return START_NOT_STICKY;}
        try{
            NotificationManager manager=getSystemService(NotificationManager.class);manager.createNotificationChannel(new NotificationChannel(CHANNEL,"BandKey VM 后台 BLE 测试",NotificationManager.IMPORTANCE_LOW));
            if(Build.VERSION.SDK_INT>=29)startForeground(NOTIFICATION,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);else startForeground(NOTIFICATION,notification());
            lease.start(SystemClock.elapsedRealtime(),BleBackgroundLease.MAX_DURATION,()->new BleBackgroundLease.Transport(){
                final BlePhonePeripheral ble=new BlePhonePeripheral(VmBleTransportService.this,19443,ignored->{},()->lease.allows(SystemClock.elapsedRealtime()));
                @Override public void start()throws Exception{ble.start();}
                @Override public BleBackgroundLease.Stats stats(){boolean connected=ble.isConnected();return new BleBackgroundLease.Stats(ble.isAdvertising(),connected,connected && ble.isStreamActive(),ble.sentBytes,ble.receivedBytes,ble.connections);}
                @Override public void close(){ble.close();}
            });
            timer.run();
        }catch(RuntimeException error){finish(BleBackgroundLease.Reason.START_FAILED);}
        return START_NOT_STICKY;
    }
    private Notification notification(){
        PendingIntent open=PendingIntent.getActivity(this,41,new Intent(this,VmLabActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Intent stop=new Intent(this,VmBleTransportService.class).setAction(STOP).setData(Uri.parse("bandkey://vm-ble/"+token)).putExtra("run",token);
        PendingIntent cancel=PendingIntent.getService(this,42,stop,PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this,CHANNEL).setSmallIcon(android.R.drawable.stat_sys_data_bluetooth).setContentTitle("BandKey 后台 BLE 测试")
            .setContentText("最多运行30分钟；仅保持传输，点击返回实验页").setContentIntent(open).setOnlyAlertOnce(true).setOngoing(true).setVisibility(Notification.VISIBILITY_SECRET)
            .addAction(new Notification.Action.Builder(null,"停止传输",cancel).build()).build();
    }
    private void updateNotification(){String state=last.stage.name();if(!state.equals(notificationState)){notificationState=state;getSystemService(NotificationManager.class).notify(NOTIFICATION,notification());}}
    private void finish(BleBackgroundLease.Reason reason){
        if(finishing)return;finishing=true;handler.removeCallbacks(timer);
        if(lease.snapshot().stage==BleBackgroundLease.Stage.IDLE)lease.start(SystemClock.elapsedRealtime(),BleBackgroundLease.MAX_DURATION,()->{throw new IllegalStateException("Transport was not started");});
        lease.stop(reason);last=lease.snapshot();
        stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();
    }
    @Override public void onTaskRemoved(Intent rootIntent){finish(BleBackgroundLease.Reason.TASK_REMOVED);}
    @Override public void onDestroy(){if(token!=null)finish(BleBackgroundLease.Reason.DESTROYED);else handler.removeCallbacks(timer);if(running==this)running=null;super.onDestroy();}
    @Override public IBinder onBind(Intent intent){return null;}
}
