package dev.bandkey.bridge;

import android.app.*;
import android.content.*;
import android.os.*;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.device.Device;
import com.huawei.wearengine.notify.*;
import dev.bandkey.aimakey.core.*;
import dev.bandkey.aimakey.field.MobileTrace;
import java.util.HashMap;

/** Isolated :aimakey process. It has no vehicle adapter, PC client, signing or Windows grant path. */
public final class AimaWearFieldService extends Service {
    static final String START = "dev.bandkey.bridge.AIMA_DIAGNOSTIC_START", STOP = "dev.bandkey.bridge.AIMA_DIAGNOSTIC_STOP";
    private static final String CHANNEL = "aimakey-wrist-diagnostic";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final WristDiagnosticSession session = new WristDiagnosticSession();
    private MobileTrace trace;
    private String status = "等待手动开始一次手环诊断；不会控车";
    private long ticket;
    private boolean expiryRecorded;
    private boolean queryPending, receiptPending;
    public final class LocalBinder extends Binder { public AimaWearFieldService service() { return AimaWearFieldService.this; } }
    private final LocalBinder binder = new LocalBinder();
    @Override public void onCreate() { super.onCreate(); trace = new MobileTrace(this, FieldTraceStore.Domain.WRIST_NOTIFICATION_TEST); }
    @Override public IBinder onBind(Intent intent) { return binder; }
    public String status() { return status + (trace.failed() ? "\n本机记录写入失败，请勿据缺失记录推断结果" : ""); }
    @SuppressWarnings("deprecation")
    boolean hostBusy() {
        try {
        for (ActivityManager.RunningServiceInfo info : getSystemService(ActivityManager.class).getRunningServices(100)) {
            String name = info.service.getClassName();
            if (name.equals(BridgeService.class.getName()) || name.equals(VmWearConfirmService.class.getName()) || name.equals(VmBleTransportService.class.getName())) return true;
        }
        return false;
        } catch (RuntimeException e) { return true; }
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel(CHANNEL, "AimaKey 一次性手环诊断", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, AimaWearFieldActivity.class), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, AimaWearFieldService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE);
        startForeground(47, new android.app.Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("AimaKey 手环诊断 · 不控车").setContentText("只记录真实手环点击，120秒内一次有效").setOngoing(true).setContentIntent(open)
            .addAction(new android.app.Notification.Action.Builder(null, "停止诊断", stop).build()).build());
        if (intent != null && START.equals(intent.getAction())) begin();
        else if (intent != null && STOP.equals(intent.getAction())) cancel();
        else cancel();
        return START_NOT_STICKY;
    }
    private void begin() {
        if (session.active(ticket, SystemClock.elapsedRealtime()) || receiptPending) return;
        if (!getSystemService(PowerManager.class).isInteractive() || getSystemService(KeyguardManager.class).isKeyguardLocked()) { status = "请在手机已解锁的前台手动开始"; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); return; }
        if (hostBusy()) { status = "Windows 手机桥接仍在运行。请在原桥接设置中手动暂停，再开始本诊断；本功能不会自动暂停。";
            trace.record(FieldTraceStore.Event.HOST_BUSY, null, null, 0); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); return; }
        String uuid = getSharedPreferences(BridgeService.PREFS, MODE_PRIVATE).getString("wearableUuid", "");
        if (uuid.isEmpty()) { status = "请先在 BandKey 主界面选择已配对手环"; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); return; }
        handler.removeCallbacksAndMessages(null); ticket = session.begin(SystemClock.elapsedRealtime(), 120_000); expiryRecorded = false;
        long run = ticket; queryPending = true; status = "正在查询所选手环；只发送本次诊断，不自动续发"; trace.record(FieldTraceStore.Event.SDK_QUERY, null, null, 0);
        handler.postDelayed(() -> expire(run), 120_000);
        handler.postDelayed(() -> { if (session.active(run, SystemClock.elapsedRealtime()) && queryPending) fail(run, -1); }, 10_000);
        try {
            HiWear.getDeviceClient(this).getCommonDevice().addOnSuccessListener(devices -> handler.post(() -> {
                if (!session.active(run, SystemClock.elapsedRealtime())) return;
                queryPending = false;
                if (hostBusy()) { fail(run, -2); return; }
                Device selected = null; for (Device device : devices) if (uuid.equals(device.getUuid())) selected = device;
                if (selected == null || !selected.isConnected() || selected.getNotifyCapability() != Device.DEVICE_CAPABILITY_SUPPORT) { trace.record(FieldTraceStore.Event.SDK_DISCONNECTED, null, null, 0); fail(run, -3); return; }
                trace.record(FieldTraceStore.Event.SDK_CONNECTED, null, null, 0); sendPrompt(selected, run);
            })).addOnFailureListener(error -> handler.post(() -> fail(run, -4)));
        } catch (RuntimeException e) { fail(run, -5); }
    }
    private void sendPrompt(Device device, long run) {
        HashMap<Integer, String> buttons = new HashMap<>(); buttons.put(NotificationConstants.BUTTON_ONE_CONTENT_KEY, "启动测试"); buttons.put(NotificationConstants.BUTTON_TWO_CONTENT_KEY, "关闭测试");
        com.huawei.wearengine.notify.Notification notification = new com.huawei.wearengine.notify.Notification.Builder()
            .setTemplateId(NotificationTemplate.NOTIFICATION_TEMPLATE_TWO_BUTTONS).setPackageName(getPackageName())
            .setTitle("AimaKey · 仅点击诊断").setText("不控制车辆 · 本次120秒有效").setButtonContents(buttons).setAction(new Action() {
                public void onResult(com.huawei.wearengine.notify.Notification n, int feedback) { handler.post(() -> {
                    if (!session.active(run, SystemClock.elapsedRealtime())) { expire(run); return; }
                    if (hostBusy()) { cancel(); status = "Windows手机桥接已恢复，本次诊断取消，未执行任何车辆动作"; return; }
                    VehicleController.Action action = session.consume(run, feedback, SystemClock.elapsedRealtime());
                    if (action == null) { expire(run); return; }
                    trace.record(FieldTraceStore.Event.WRIST_GESTURE, action, null, feedback);
                    status = "已收到真实手环" + (action == VehicleController.Action.START ? "启动测试" : "关闭测试") + "；车辆指令发送数：0。正在保存本机记录。";
                    handler.removeCallbacksAndMessages(null); receiptPending = true; sendReceipt(device, action, run);
                    handler.postDelayed(() -> finishReceipt(run, action, false, -10), 5_000);
                }); }
                public void onError(com.huawei.wearengine.notify.Notification n, int code, String message) { handler.post(() -> fail(run, code)); }
            }).build();
        status = "正在发送一次手环诊断通知";
        try { HiWear.getNotifyClient(this).notify(device, notification).addOnSuccessListener(ignored -> handler.post(() -> {
            if (!session.active(run, SystemClock.elapsedRealtime())) return;
            trace.record(FieldTraceStore.Event.SDK_ACCEPTED, null, null, 1); status = "SDK已接受；请锁屏放进口袋，在手环点击启动测试或关闭测试。发送成功不等于收到点击。";
        })).addOnFailureListener(error -> handler.post(() -> fail(run, -6))); } catch (RuntimeException e) { fail(run, -7); }
    }
    private void sendReceipt(Device device, VehicleController.Action action, long run) {
        com.huawei.wearengine.notify.Notification receipt = new com.huawei.wearengine.notify.Notification.Builder().setPackageName(getPackageName())
            .setTemplateId(NotificationTemplate.NOTIFICATION_TEMPLATE_NO_BUTTON).setTitle("AimaKey · 手机已收到")
            .setText((action == VehicleController.Action.START ? "启动测试" : "关闭测试") + " · 未控制车辆").setAction(new Action() {
                public void onResult(com.huawei.wearengine.notify.Notification n, int feedback) {}
                public void onError(com.huawei.wearengine.notify.Notification n, int code, String message) { handler.post(() -> finishReceipt(run, action, false, code)); }
            }).build();
        try { HiWear.getNotifyClient(this).notify(device, receipt).addOnSuccessListener(ignored -> handler.post(() -> finishReceipt(run, action, true, 2)))
            .addOnFailureListener(error -> handler.post(() -> finishReceipt(run, action, false, -8))); } catch (RuntimeException e) { finishReceipt(run, action, false, -9); }
    }
    private void finishReceipt(long run, VehicleController.Action action, boolean accepted, int code) {
        if (!receiptPending || run != ticket) return;
        receiptPending = false; trace.record(accepted ? FieldTraceStore.Event.SDK_ACCEPTED : FieldTraceStore.Event.SDK_ERROR, action, null, code);
        handler.removeCallbacksAndMessages(null);
        trace.flush(() -> {
            if (run != ticket || session.state() == WristDiagnosticSession.State.CANCELLED) return;
            status += trace.failed() ? " 本机记录写入失败。" : " 记录已写入手机本地。";
            stopForeground(STOP_FOREGROUND_REMOVE); stopSelf();
        });
    }
    private void expire(long run) {
        session.active(run, SystemClock.elapsedRealtime());
        if (session.state() == WristDiagnosticSession.State.EXPIRED && !expiryRecorded) { expiryRecorded = true; status = "本次已过期，旧按钮不再接受；没有自动续发";
            trace.record(FieldTraceStore.Event.EXPIRED, null, null, 0); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); }
    }
    private void fail(long run, int code) {
        if (!session.active(run, SystemClock.elapsedRealtime())) return;
        session.fail(run); status = "诊断未完成，SDK／连接错误代码：" + code + "；未发送车辆指令";
        trace.record(FieldTraceStore.Event.SDK_ERROR, null, null, code); handler.removeCallbacksAndMessages(null); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf();
    }
    public void cancel() { receiptPending = false; session.cancel(); handler.removeCallbacksAndMessages(null); status = "本次诊断已取消，不自动续发";
        trace.record(FieldTraceStore.Event.CANCELLED, null, null, 0); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); }
    @Override public void onDestroy() { receiptPending = false; session.cancel(); handler.removeCallbacksAndMessages(null); trace.close(); super.onDestroy(); }
}
