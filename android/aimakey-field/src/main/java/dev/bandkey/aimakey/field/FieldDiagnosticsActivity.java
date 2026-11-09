package dev.bandkey.aimakey.field;

import android.app.Activity;
import android.content.Intent;
import android.os.*;
import android.widget.*;
import dev.bandkey.aimakey.core.FieldTraceStore;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;

/** A user-selected document export; no automatic upload or recipient. */
public final class FieldDiagnosticsActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private TextView output;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state); LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        int pad = Math.round(20 * getResources().getDisplayMetrics().density); root.setPadding(pad, pad, pad, pad);
        ScrollView scroll = new ScrollView(this); scroll.addView(root); setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) { android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars()); root.setPadding(pad + bars.left, pad + bars.top, pad + bars.right, pad + bars.bottom); }
            else root.setPadding(pad, pad + insets.getSystemWindowInsetTop(), pad, pad + insets.getSystemWindowInsetBottom()); return insets;
        }); scroll.requestApplyInsets();
        TextView intro = new TextView(this); intro.setText("手机本地诊断\n无需电脑在旁。记录手环诊断和本应用处理过程，不读取爱玛 App 的私有日志，不发送实车指令。\n最近保留8次记录，每次最多128 KiB。离线保存，选择导出后才复制到你指定的位置。"); root.addView(intro);
        Button export = new Button(this); export.setText("导出诊断 ZIP"); root.addView(export);
        export.setOnClickListener(view -> startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType("application/zip").putExtra(Intent.EXTRA_TITLE, "aimakey-field-diagnostics.zip"), 61));
        output = new TextView(this); output.setTextIsSelectable(true); root.addView(output); refresh();
    }
    private void refresh() { worker.execute(() -> {
        try { FieldTraceStore store = MobileTrace.open(this); String text = "本包记录数：" + store.count() + "\nscreenFlags: 1=亮屏，2=锁定，4=电池优化豁免，8=省电模式，-1=未知\n\n"
            + new String(store.latest().bytes(), StandardCharsets.UTF_8); runOnUiThread(() -> output.setText(text)); }
        catch (IOException e) { runOnUiThread(() -> output.setText("诊断暂不可读取；未推断任何操作成功。")); }
    }); }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data); if (request != 61 || result != RESULT_OK || data == null || data.getData() == null) return;
        android.net.Uri destination = data.getData(); worker.execute(() -> {
            try (OutputStream stream = getContentResolver().openOutputStream(destination)) {
                if (stream == null) throw new IOException("NO_OUTPUT"); MobileTrace.open(this).exportZip(stream);
                runOnUiThread(() -> Toast.makeText(this, "已导出到你选择的位置", Toast.LENGTH_LONG).show());
            } catch (IOException e) { runOnUiThread(() -> Toast.makeText(this, "导出失败，原本机记录保留", Toast.LENGTH_LONG).show()); }
        });
    }
    @Override protected void onDestroy() { worker.shutdown(); super.onDestroy(); }
}
