package dev.bandkey.aimakey;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.os.*;
import android.view.Gravity;
import android.widget.*;
import dev.bandkey.aimakey.core.*;
import java.util.function.Consumer;

/** Replaceable two-action page model on the phone; this is not installable Band 11 Pro UI. */
public final class AimaActivity extends Activity {
    private AimaService service;
    private boolean bound;
    private TextView status, diagnostics;
    private Button start, stop;
    private final Consumer<String> observer = value -> runOnUiThread(() -> status.setText(value));
    private final ServiceConnection connection = new ServiceConnection() {
        public void onServiceConnected(ComponentName name, IBinder binder) {
            service = ((AimaService.LocalBinder) binder).service(); service.observe(observer); start.setEnabled(true); stop.setEnabled(true);
        }
        public void onServiceDisconnected(ComponentName name) { service = null; start.setEnabled(false); stop.setEnabled(false); }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        ScrollView scroll = new ScrollView(this); LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int padding = Math.round(20 * getResources().getDisplayMetrics().density);
        root.setPadding(padding, padding, padding, padding); scroll.addView(root); setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars());
                root.setPadding(padding + bars.left, padding + bars.top, padding + bars.right, padding + bars.bottom);
            } else root.setPadding(padding + insets.getSystemWindowInsetLeft(), padding + insets.getSystemWindowInsetTop(),
                padding + insets.getSystemWindowInsetRight(), padding + insets.getSystemWindowInsetBottom());
            return insets;
        });
        scroll.requestApplyInsets();
        TextView title = text("AimaKey", 30); root.addView(title);
        root.addView(text("仅模拟 · 不连接真实车辆\n手环页面尚无可安装入口，此处为替代交互模型", 16));
        LinearLayout face = new LinearLayout(this); face.setOrientation(LinearLayout.VERTICAL); face.setGravity(Gravity.CENTER);
        face.setPadding(24, 24, 24, 24); face.setBackgroundColor(Color.rgb(20, 28, 36)); root.addView(face);
        face.addView(text("AimaKey · 模拟手环页面", 18));
        status = text("正在连接本地模拟服务…", 16); status.setMinHeight(100); face.addView(status);
        start = button(face, "启动", () -> { if (service != null) service.simulate(VehicleController.Action.START, false); });
        stop = button(face, "关闭", () -> {
            if (service == null) return;
            new AlertDialog.Builder(this).setTitle("确认关闭模拟车辆")
                .setMessage("关闭需要新鲜的车辆静止状态。模拟验证通过也不代表实车可安全关闭。")
                .setNegativeButton("取消", null).setPositiveButton("确认关闭", (d, w) -> {
                    if (service != null) service.simulate(VehicleController.Action.STOP, true);
                }).show();
        });
        start.setEnabled(false); stop.setEnabled(false);
        root.addView(text("测试场景", 18));
        Spinner scenarios = new Spinner(this);
        String[] names = {"正常回执", "车辆未连接", "车辆移动中", "静止状态未知", "无回执超时", "仅接收未完成", "操作失败"};
        scenarios.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, names)); root.addView(scenarios);
        scenarios.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            public void onItemSelected(android.widget.AdapterView<?> p, android.view.View v, int i, long id) {
                if (service != null) service.setScenario(MockVehicleController.Scenario.values()[i]);
            }
            public void onNothingSelected(android.widget.AdapterView<?> p) {}
        });
        button(root, "开启锁屏后台模拟", this::startBackground);
        button(root, "停止后台模拟", () -> { if (service != null) service.stopBackground(); });
        button(root, "查看／导出手机本地诊断", () -> startActivity(new Intent(this, dev.bandkey.aimakey.field.FieldDiagnosticsActivity.class)));
        button(root, "打开 BandKey 手环真实点击诊断", () -> {
            try { startActivity(new Intent().setClassName("dev.bandkey.bridge", "dev.bandkey.bridge.MainActivity").putExtra("bandkeyAimaLab", true)); }
            catch (RuntimeException e) { status.setText("请先安装含 AimaKey 手环诊断入口的 BandKey 0.9.6-field 或更新版本"); }
        });
        diagnostics = text("Wear Engine：独立包尚未配置服务授权。BandKey 的授权不自动适用于本包。", 14); root.addView(diagnostics);
        button(root, "只读能力诊断", () -> new WearCapabilityProbe(this).run(value -> runOnUiThread(() -> diagnostics.setText(value))));
        button(root, "请求 Wear Engine 设备管理授权", () -> new WearCapabilityProbe(this).authorize(value -> runOnUiThread(() -> diagnostics.setText(value))));
    }
    private TextView text(String value, int size) { TextView view = new TextView(this); view.setText(value); view.setTextSize(size); view.setPadding(0, 10, 0, 10); return view; }
    private Button button(LinearLayout parent, String label, Runnable action) {
        Button view = new Button(this); view.setText(label); view.setMinHeight(64); view.setOnClickListener(v -> action.run()); parent.addView(view); return view;
    }
    private void startBackground() {
        java.util.ArrayList<String> permissions = new java.util.ArrayList<>();
        if (Build.VERSION.SDK_INT >= 31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED)
            permissions.add(Manifest.permission.BLUETOOTH_CONNECT);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            permissions.add(Manifest.permission.POST_NOTIFICATIONS);
        if (!permissions.isEmpty()) { requestPermissions(permissions.toArray(new String[0]), 42); return; }
        try { startForegroundService(new Intent(this, AimaService.class)); }
        catch (RuntimeException e) { status.setText("后台启动被系统拒绝，请在前台检查权限后重试"); }
    }
    @Override protected void onStart() { super.onStart(); bound = bindService(new Intent(this, AimaService.class), connection, BIND_AUTO_CREATE); }
    @Override protected void onStop() {
        if (service != null) service.unobserve(observer);
        if (bound) unbindService(connection); bound = false; service = null; super.onStop();
    }
}
