package dev.bandkey.bridge;

import android.Manifest;
import android.app.*;
import android.bluetooth.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.net.Uri;
import android.os.*;
import java.net.URI;
import java.util.UUID;
import java.util.concurrent.*;
import org.json.*;

/** Explicit VM-only, real-wearable confirmation session. It never accepts phone notification approvals. */
public final class VmWearConfirmService extends Service {
    private static final String START="dev.bandkey.bridge.VM_WEAR_START",STOP="dev.bandkey.bridge.VM_WEAR_STOP",CHANNEL="bandkey-vm-wear";
    private static final int NOTIFICATION=43;
    private static volatile VmWearConfirmService running;
    private static volatile String lastSummary="后台手环确认未启动。";
    private static volatile String lastReport;
    private final BleBackgroundLease lease=new BleBackgroundLease();
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final ScheduledExecutorService worker=Executors.newSingleThreadScheduledExecutor();
    private volatile VmWearSession<JSONObject> session;
    private BlePhonePeripheral ble;
    private String token,pcSaved,wearableUuid,noticeState="";
    private volatile String wearableStatus="";
    private boolean finishing;
    private boolean fixedPhoneMode;
    private WearableConnectionSource bandConnection;
    private VmFixedPhoneClient fixedClient;
    private int observedFixedRenewals;
    static boolean active(){VmWearConfirmService s=running;return s!=null && !s.finishing && s.lease.allows(SystemClock.elapsedRealtime());}
    static boolean requestStart(Activity activity,Dialog consent){
        boolean focused=activity.hasWindowFocus() || (consent!=null && consent.getOwnerActivity()==activity && consent.isShowing() && consent.getWindow()!=null && consent.getWindow().getDecorView().hasWindowFocus());
        if(active() || VmBleTransportService.active() || activity.isFinishing() || !focused ||
            !activity.getSystemService(PowerManager.class).isInteractive() || activity.getSystemService(KeyguardManager.class).isKeyguardLocked())return false;
        activity.startForegroundService(new Intent(activity,VmWearConfirmService.class).setAction(START).putExtra("run",UUID.randomUUID().toString()));return true;
    }
    static void stopCurrent(){VmWearConfirmService s=running;if(s!=null)s.finish("user");}
    static String summary(){return lastSummary;}
    static String report(){String value=lastReport;if(value==null)throw new IllegalStateException("No VM wearable run");return value;}
    private void updateReport(String reason){
        try{
            VmWearSession<JSONObject> current=session;BleBackgroundLease.Snapshot stats=lease.snapshot();
            lastReport=new JSONObject().put("schema","bandkey.vm-wear-background.v1").put("state",current==null?"STOPPED":current.state().name()).put("reason",reason)
                .put("remainingMilliseconds",stats.remaining).put("streams",stats.streams).put("disconnects",stats.disconnects)
                .put("pollResponsesObserved",current==null?0:current.polls()).put("promptAttempts",current==null?0:current.prompts())
                .put("authorizationResponsesObserved",current==null?0:current.delivered()).put("denialResponsesObserved",current==null?0:current.denied())
                .put("fixedPhoneMode",fixedPhoneMode).put("fixedStateResponses",fixedClient==null?0:fixedClient.reports()).put("requestRenewalResponses",fixedClient==null?0:fixedClient.renewals())
                .put("connectionSource",bandConnection==null?JSONObject.NULL:bandConnection.report())
                .put("fixedPhoneDiagnostic",fixedClient==null?JSONObject.NULL:fixedClient.report())
                .put("independentlyValidated",false).put("windowsUnlockObserved",false).toString(2);
        }catch(JSONException ignored){}
    }
    private boolean bluetoothAllowed(){
        try{
            if(Build.VERSION.SDK_INT>=31 && (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)!=PackageManager.PERMISSION_GRANTED || checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE)!=PackageManager.PERMISSION_GRANTED))return false;
            BluetoothManager manager=getSystemService(BluetoothManager.class);BluetoothAdapter adapter=manager==null?null:manager.getAdapter();return adapter!=null && adapter.isEnabled();
        }catch(RuntimeException error){return false;}
    }
    private boolean configurationCurrent(){
        return lease.allows(SystemClock.elapsedRealtime()) && getSystemService(NotificationManager.class).areNotificationsEnabled() &&
            pcSaved.equals(getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE).getString("pc",null)) &&
            fixedPhoneMode==getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE).getBoolean("fixedPhoneMode",false) &&
            wearableUuid.equals(getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getString("wearableUuid","")) &&
            !getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getBoolean("enabled",false);
    }
    @Override public void onCreate(){super.onCreate();running=this;}
    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent!=null && STOP.equals(intent.getAction())){
            if(token!=null && token.equals(intent.getStringExtra("run")))finish("user");else if(token==null)stopSelf();return START_NOT_STICKY;
        }
        if(token!=null)return START_NOT_STICKY;
        String requested=intent==null?null:intent.getStringExtra("run");
        if(intent==null || !START.equals(intent.getAction()) || requested==null || !requested.matches("[a-f0-9-]{36}")){stopSelf();return START_NOT_STICKY;}
        token=requested;
        try{
            if(!bluetoothAllowed() || VmBleTransportService.active() || !getSystemService(NotificationManager.class).areNotificationsEnabled())throw new IllegalStateException("startup-unavailable");
            NotificationManager manager=getSystemService(NotificationManager.class);manager.createNotificationChannel(new NotificationChannel(CHANNEL,"BandKey VM 后台手环确认",NotificationManager.IMPORTANCE_LOW));
            if(Build.VERSION.SDK_INT>=29)startForeground(NOTIFICATION,notification("正在启动"),ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);else startForeground(NOTIFICATION,notification("正在启动"));
            pcSaved=getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE).getString("pc",null);wearableUuid=getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getString("wearableUuid","");
            if(pcSaved==null || wearableUuid.isEmpty() || getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getBoolean("enabled",false))throw new IllegalStateException("configuration-unavailable");
            JSONObject config=new JSONObject(pcSaved);URI endpoint=new URI(config.getString("endpoint"));
            if(!"127.0.0.1".equals(endpoint.getHost()) || endpoint.getPort()!=19443)throw new IllegalStateException("BLE-loopback-required");
            final VmRelayClient client=new VmRelayClient(config,new Identity("bandkey-vm-lab-identity-v1"));
            fixedPhoneMode=getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE).getBoolean("fixedPhoneMode",false);
            if(fixedPhoneMode){bandConnection=new WearableConnectionSource(this,wearableUuid);fixedClient=new VmFixedPhoneClient(client,wearableUuid);bandConnection.start();}
            if(!lease.start(SystemClock.elapsedRealtime(),BleBackgroundLease.MAX_DURATION,()->new BleBackgroundLease.Transport(){
                @Override public void start()throws Exception{ble=new BlePhonePeripheral(VmWearConfirmService.this,19443,ignored->{},()->lease.allows(SystemClock.elapsedRealtime()));ble.start();}
                @Override public BleBackgroundLease.Stats stats(){boolean connected=ble.isConnected();return new BleBackgroundLease.Stats(ble.isAdvertising(),connected,connected && ble.isStreamActive(),ble.sentBytes,ble.receivedBytes,ble.connections);}
                @Override public void close(){if(ble!=null)ble.close();}
            }))throw new IllegalStateException("transport-start-failed");
            session=new VmWearSession<>(()->System.currentTimeMillis()/1000,SystemClock::elapsedRealtime,BleBackgroundLease.MAX_DURATION,this::configurationCurrent,()->ble!=null && ble.isConnected() && (!fixedPhoneMode || fixedClient.ready() && bandConnection.connected()),new VmWearSession.Client<JSONObject>(){
                @Override public VmWearSession.Request<JSONObject> poll()throws Exception{
                    JSONObject prompt=client.poll(fixedPhoneMode?3000:0);if(prompt==null)return null;
                    WindowsGrantWire.Challenge body=WindowsGrantWire.read(android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP));
                    return new VmWearSession.Request<>(prompt,prompt.getString("challenge"),(body.expires-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS)/1000);
                }
                @Override public VmWearSession.Result decide(JSONObject prompt,boolean approved)throws Exception{
                    JSONObject result=client.decide(prompt,approved,fixedPhoneMode?3000:0);
                    if(result.optBoolean("windowsUnlocked"))throw new SecurityException("Unobserved Windows unlock claim");
                    if(approved && result.optBoolean("authorized") && result.optBoolean("submitted"))return VmWearSession.Result.DELIVERED;
                    if(!approved && !result.optBoolean("authorized") && !result.optBoolean("submitted") && "user-denied".equals(result.optString("code")))return VmWearSession.Result.DENIED;
                    return VmWearSession.Result.REJECTED;
                }
            },(gate,ticket,allowed,clicked)->{
                wearableStatus="";new WearableChannel(this).prompt(wearableUuid,"VM 后台解锁确认","仅此 VM · 本次请求30秒内有效",gate,ticket,allowed,clicked,message->wearableStatus=message.substring(0,Math.min(160,message.length())));
            },task->worker.execute(()->withWake(task)));
            worker.scheduleWithFixedDelay(()->withWake(()->{
                VmWearSession<JSONObject> current=session;
                if(fixedPhoneMode && ble!=null && ble.isConnected()){
                    fixedClient.pulse(bandConnection.sample());
                    // A brief HTTP interruption blocks submission through the link predicate but
                    // must not consume the still-current notification. A refreshed native request
                    // cancels the old notification. Unknown/brief loss only blocks its submission.
                    int renewals=fixedClient.renewals();
                    if(renewals!=observedFixedRenewals){if(current!=null)current.invalidatePending();}
                    observedFixedRenewals=renewals;
                }
                if(current!=null)current.poll();
            }),0,2,TimeUnit.SECONDS);
            handler.post(timer);
        }catch(Exception error){finish("startup-"+error.getClass().getSimpleName());}
        return START_NOT_STICKY;
    }
    private void withWake(Runnable action){
        PowerManager.WakeLock wake=getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"BandKey:VmWear");
        try{wake.acquire(45000);action.run();}finally{if(wake.isHeld())wake.release();}
    }
    private final Runnable timer=new Runnable(){@Override public void run(){
        if(finishing)return;lease.tick(SystemClock.elapsedRealtime(),bluetoothAllowed());
        if(lease.snapshot().stage==BleBackgroundLease.Stage.STOPPED){finish(lease.snapshot().reason.name());return;}
        VmWearSession<JSONObject> current=session;if(current!=null && !current.active()){finish("session-ended");return;}
        BleBackgroundLease.Snapshot stats=lease.snapshot();String state=current==null?"启动中":label(current.state());
        lastSummary="VM 后台手环确认："+state+"\n剩余 "+((stats.remaining+999)/1000)+" 秒；完整流 "+stats.streams+"。\n仅真实手环本次点击可确认；交付签名不代表 Windows 已解锁。";
        if(!wearableStatus.isEmpty())lastSummary+="\n最近手环状态："+wearableStatus;
        if(fixedPhoneMode)lastSummary+="\n手机固定在电脑旁："+fixedClient.status()+"\n状态报告 "+fixedClient.reports()+"；刷新请求 "+fixedClient.renewals()+"。";
        updateReport("running");
        if(!state.equals(noticeState)){noticeState=state;getSystemService(NotificationManager.class).notify(NOTIFICATION,notification(state));}
        handler.postDelayed(this,1000);
    }};
    private static String label(VmWearSession.State state){switch(state){
        case WAIT_LINK:return "等待电脑 BLE";case WAIT_REQUEST:return "等待 VM 请求";case WAIT_CLICK:return "等待手环明确点击";
        case QUEUED:return "正在提交本次决定";case DELIVERED:return "授权已交付，请观察 VM";case DENIED:return "本次请求已拒绝";
        case POLL_ERROR:return "通信中断，等待重试";case SUBMIT_ERROR:return "本次决定提交失败，不重发";case REJECTED:return "请求已失效或被拒绝";default:return "已停止";
    }}
    private Notification notification(String state){
        PendingIntent open=PendingIntent.getActivity(this,43,new Intent(this,VmLabActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Intent stop=new Intent(this,VmWearConfirmService.class).setAction(STOP).setData(Uri.parse("bandkey://vm-wear/"+token)).putExtra("run",token);
        PendingIntent cancel=PendingIntent.getService(this,44,stop,PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this,CHANNEL).setSmallIcon(android.R.drawable.stat_sys_data_bluetooth).setContentTitle("BandKey VM 后台手环确认")
            .setContentText(state+"；最多30分钟").setContentIntent(open).setOnlyAlertOnce(true).setOngoing(true).setVisibility(Notification.VISIBILITY_SECRET)
            .addAction(new Notification.Action.Builder(null,"停止确认及传输",cancel).build()).build();
    }
    private void finish(String reason){
        if(finishing)return;finishing=true;handler.removeCallbacks(timer);VmWearSession<JSONObject> current=session;if(current!=null)current.stop();
        if(fixedClient!=null)fixedClient.stop();if(bandConnection!=null)bandConnection.stop();
        worker.shutdownNow();lease.stop(BleBackgroundLease.Reason.USER);lastSummary="VM 后台手环确认已停止："+reason+"。已交付的签名无法撤回，原生有效期与一次性检查仍适用。";
        updateReport(reason);
        stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();
    }
    @Override public void onTaskRemoved(Intent rootIntent){finish("task-removed");}
    @Override public void onDestroy(){finish("destroyed");if(running==this)running=null;super.onDestroy();}
    @Override public IBinder onBind(Intent intent){return null;}
}
