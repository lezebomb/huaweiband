package dev.bandkey.aimakey;

import android.app.*;
import android.content.Intent;
import android.os.*;
import android.util.Log;
import dev.bandkey.aimakey.core.*;
import dev.bandkey.aimakey.field.MobileTrace;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;

/** Separate UID, preferences, foreground notification and lifecycle from BandKey. Mock adapter only. */
public final class AimaService extends Service {
    final Handler handler = new Handler(Looper.getMainLooper());
    private final List<Consumer<String>> observers = new ArrayList<>();
    private AimaCoordinator coordinator;
    private MockVehicleController controller;
    private AimaPageModel page;
    private MobileTrace trace;
    private String status = "模拟车辆已连接 · 实车接口尚未接入";
    private boolean foreground;
    public final class LocalBinder extends Binder { public AimaService service() { return AimaService.this; } }
    private final LocalBinder binder = new LocalBinder();
    private final Runnable ticker = new Runnable() {
        @Override public void run() { coordinator.tick(); page.tick(System.currentTimeMillis()); handler.postDelayed(this, 250); }
    };
    @Override public void onCreate() {
        super.onCreate();
        trace = new MobileTrace(this, FieldTraceStore.Domain.PHONE_MOCK);
        AimaCoordinator.Clock clock = new AimaCoordinator.Clock() {
            public long wallMs() { return System.currentTimeMillis(); }
            public long elapsedMs() { return SystemClock.elapsedRealtime(); }
        };
        byte[] random = new byte[16]; new SecureRandom().nextBytes(random);
        StringBuilder nonce = new StringBuilder(); for (byte b : random) nonce.append(String.format(java.util.Locale.ROOT, "%02x", b & 255));
        controller = new MockVehicleController(clock, (ms, work) -> handler.postDelayed(work, ms));
        page = new AimaPageModel(this::receiveTrustedLocal);
        coordinator = new AimaCoordinator(nonce.toString(), controller, clock,
            request -> getSharedPreferences("aima-journal", MODE_PRIVATE).edit()
                .putString("session", request.session()).putLong("sequence", request.sequence())
                .putString("requestId", request.id()).commit(),
            result -> { trace.record(FieldTraceStore.Event.REQUEST_RESULT, result.action(), result.stage(), 0);
                page.receive(AimaProtocol.encodeReply(nonce.toString(), result)); setStatus(describe(result)); });
        page.connected(nonce.toString(), true);
        handler.post(ticker);
    }
    @Override public IBinder onBind(Intent intent) { return binder; }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        // An Intent can only keep this service alive; it can never contain a vehicle action.
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("aimakey-background", "AimaKey 后台模拟", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, AimaActivity.class), PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, "aimakey-background")
            .setSmallIcon(android.R.drawable.stat_notify_sync).setContentTitle("AimaKey · 仅模拟")
            .setContentText("后台测试运行中，不连接真实车辆").setContentIntent(open).setOngoing(true).build();
        startForeground(2101, notification); foreground = true;
        trace.record(FieldTraceStore.Event.SERVICE_STARTED, null, null, 0);
        return START_NOT_STICKY; // No restoration of queued commands after process death.
    }
    public String status() { return status; }
    public String session() { return coordinator.session(); }
    public boolean isForeground() { return foreground; }
    public void observe(Consumer<String> observer) { observers.add(observer); observer.accept(status); }
    public void unobserve(Consumer<String> observer) { observers.remove(observer); }
    public void simulate(VehicleController.Action action, boolean stopConfirmed) {
        trace.record(FieldTraceStore.Event.PHONE_GESTURE, action, null, 0);
        String code = page.activate(action, stopConfirmed, System.currentTimeMillis());
        if (!"REQUESTED".equals(code)) setStatus("[模拟] 已拒绝：" + code);
    }
    /** Local binder/verified SDK peer only. This method is not an exported Intent receiver. */
    void receiveTrustedLocal(byte[] bytes) {
        try {
            AimaCoordinator.Result result = coordinator.submit(AimaProtocol.decode(bytes));
            page.receive(AimaProtocol.encodeReply(session(), result));
            if (result.stage() == AimaCoordinator.Stage.REJECTED || result.stage() == AimaCoordinator.Stage.ACCEPTED)
                { trace.record(FieldTraceStore.Event.REQUEST_RESULT, result.action(), result.stage(), 0); setStatus(describe(result)); }
        } catch (IllegalArgumentException e) { setStatus("模拟请求被拒绝：协议格式错误"); }
    }
    public void setScenario(MockVehicleController.Scenario scenario) {
        if (scenario == MockVehicleController.Scenario.DISCONNECTED) coordinator.disconnect();
        controller.setScenario(scenario);
        setStatus("模拟场景：" + scenario + " · 实车接口尚未接入");
    }
    VehicleController.Snapshot snapshot() { return controller.getVehicleConnectionState(); }
    AimaCoordinator.Result lastResult() { return coordinator.getLastOperationResult(); }
    int invocations() { return controller.invocations(); }
    public void stopBackground() { coordinator.disconnect(); foreground = false; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); }
    private void setStatus(String value) {
        status = value;
        Log.i("AimaKey", value); // Controlled codes only: no packet bodies, device IDs or vehicle/account data.
        for (Consumer<String> observer : new ArrayList<>(observers)) observer.accept(value);
    }
    private static String describe(AimaCoordinator.Result result) {
        String prefix = result.simulation() ? "[模拟] " : "";
        if ("ALREADY_IN_REQUESTED_STATE".equals(result.code())) return prefix + "当前已是目标电源状态，未发送新动作";
        return prefix + switch (result.stage()) {
            case ACCEPTED -> "已接受，尚未发送";
            case REQUEST_SENT -> "请求已发送，尚未确认车辆接收";
            case VEHICLE_RECEIVED -> "车辆已接收，尚未确认动作完成";
            case CONFIRMED -> result.action() == VehicleController.Action.START ? "启动态已确认" : "关闭态已确认";
            case TIMED_OUT -> "等待超时，结果未知，不自动重试";
            case FAILED -> "失败／结果未知：" + result.code();
            case REJECTED -> "已拒绝：" + result.code();
        };
    }
    @Override public void onDestroy() {
        coordinator.disconnect(); trace.close(); handler.removeCallbacksAndMessages(null); observers.clear(); super.onDestroy();
    }
}
