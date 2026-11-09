package dev.bandkey.bridge;

import android.app.Activity;
import android.content.*;
import android.os.*;
import android.widget.*;

/** Runs in :aimakey with the diagnostic service. Navigation never sends a notification. */
public final class AimaWearFieldActivity extends Activity {
    private AimaWearFieldService service;
    private boolean bound;
    private TextView status;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable render = new Runnable() { public void run() { if (service != null) status.setText(service.status()); handler.postDelayed(this, 500); } };
    private final ServiceConnection connection = new ServiceConnection() {
        public void onServiceConnected(ComponentName name, IBinder binder) { service = ((AimaWearFieldService.LocalBinder) binder).service(); }
        public void onServiceDisconnected(ComponentName name) { service = null; }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state); LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        int pad = Math.round(20 * getResources().getDisplayMetrics().density); root.setPadding(pad, pad, pad, pad);
        ScrollView scroll = new ScrollView(this); scroll.addView(root); setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> { if (Build.VERSION.SDK_INT >= 30) { android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars()); root.setPadding(pad + bars.left, pad + bars.top, pad + bars.right, pad + bars.bottom); }
            else root.setPadding(pad, pad + insets.getSystemWindowInsetTop(), pad, pad + insets.getSystemWindowInsetBottom()); return insets; }); scroll.requestApplyInsets();
        TextView intro = new TextView(this); intro.setText("AimaKey · 真实手环点击诊断\n本次只发送一条120秒双按钮通知。手环点击后，手机本地记录真实点击并尝试返回一条收件通知；不会启停车辆，不会解锁电脑。\n这是临时通信诊断，不是最终常驻页面。Windows手机桥接运行时会拒绝诊断，请先在原设置中手动暂停。之后可恢复原桥接。"); root.addView(intro);
        status = new TextView(this); status.setText("等待手动开始"); root.addView(status);
        button(root, "发送一次手环诊断，再锁屏放入口袋", () -> {
            try { startForegroundService(new Intent(this, AimaWearFieldService.class).setAction(AimaWearFieldService.START)); }
            catch (RuntimeException e) { status.setText("系统未允许后台诊断，请检查本应用的附近设备和通知权限"); }
        });
        button(root, "取消本次诊断", () -> { if (service != null) service.cancel(); });
        button(root, "查看／导出本机记录", () -> startActivity(new Intent(this, dev.bandkey.aimakey.field.FieldDiagnosticsActivity.class)));
        button(root, "打开原 Windows 手机桥接设置", () -> startActivity(new Intent(this, MainActivity.class).putExtra("bandkeyVmLab", true)));
    }
    private void button(LinearLayout root, String text, Runnable action) { Button button = new Button(this); button.setText(text); root.addView(button); button.setOnClickListener(v -> action.run()); }
    @Override protected void onStart() { super.onStart(); bound = bindService(new Intent(this, AimaWearFieldService.class), connection, BIND_AUTO_CREATE); handler.post(render); }
    @Override protected void onStop() { handler.removeCallbacks(render); if (bound) unbindService(connection); bound = false; service = null; super.onStop(); }
}
