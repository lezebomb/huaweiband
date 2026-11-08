package dev.bandkey.aimakey;

import android.app.Activity;
import android.os.Handler;
import android.os.Looper;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.auth.AuthCallback;
import com.huawei.wearengine.auth.Permission;
import com.huawei.wearengine.device.Device;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

/** Public Android SDK read-only queries. No app installation or notification sending. */
final class WearCapabilityProbe {
    private final Activity activity;
    WearCapabilityProbe(Activity activity) { this.activity = activity; }
    private boolean configured() {
        try {
            Object id = activity.getPackageManager().getApplicationInfo(activity.getPackageName(), android.content.pm.PackageManager.GET_META_DATA)
                .metaData.get("com.huawei.hms.client.appid");
            return id != null && id.toString().matches("[0-9]+");
        } catch (Exception e) { return false; }
    }
    void authorize(Consumer<String> output) {
        if (!configured()) { output.accept("本独立包尚未取得 Wear Engine App ID／包名／签名授权，不能借用 BandKey 身份。"); return; }
        try {
            HiWear.getAuthClient(activity).requestPermission(new AuthCallback() {
                public void onOk(Permission[] permissions) { output.accept("授权回调完成；请执行只读能力诊断。"); }
                public void onCancel() { output.accept("用户取消授权"); }
            }, Permission.DEVICE_MANAGER).addOnFailureListener(error -> output.accept("Wear Engine 设备管理授权失败；请检查包名、签名和服务开通状态"));
        } catch (RuntimeException e) { output.accept("Wear Engine 授权调用失败"); }
    }
    void run(Consumer<String> output) {
        if (!configured()) { output.accept("本包 Wear Engine 未配置。现有 BandKey 历史诊断不等于本包获得授权；参阅能力报告。"); return; }
        AtomicBoolean completed = new AtomicBoolean();
        Consumer<String> finish = value -> { if (completed.compareAndSet(false, true)) output.accept(value); };
        new Handler(Looper.getMainLooper()).postDelayed(() -> finish.accept("设备查询超时；未发送任何操作，可手动重试诊断"), 10_000);
        try {
            HiWear.getDeviceClient(activity).getCommonDevice().addOnSuccessListener(devices -> {
                StringBuilder text = new StringBuilder("SDK 5.0.3.304 · 只读诊断\n");
                for (Device device : devices) text.append("型号：").append(device.getModel()).append("\n连接：").append(device.isConnected())
                    .append("\nP2P 原始值：").append(device.getP2pCapability()).append("\nNotify 原始值：").append(device.getNotifyCapability()).append('\n');
                text.append("能力支持值为 0；P2P 支持不能证明支持安装 App。\nAndroid 的 queryDeviceCapability 官方目前只文档化 CBTI(128)，不把 ArkTS 的安装能力 ID(14) 当成 Android 受支持接口。");
                finish.accept(text.toString());
            }).addOnFailureListener(error -> finish.accept("Wear Engine 查询失败；无能力结论"));
        } catch (RuntimeException e) { finish.accept("Wear Engine 不可用；无能力结论"); }
    }
}
