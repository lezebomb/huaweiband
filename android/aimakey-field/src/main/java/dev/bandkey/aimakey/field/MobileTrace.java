package dev.bandkey.aimakey.field;

import android.app.KeyguardManager;
import android.content.Context;
import android.os.*;
import dev.bandkey.aimakey.core.*;
import java.io.IOException;
import java.util.concurrent.*;

/** Own-pipeline recording works in a pocket. It does not request READ_LOGS or read another app. */
public final class MobileTrace implements AutoCloseable {
    private final Context context;
    private final ExecutorService writer = Executors.newSingleThreadExecutor();
    private FieldTraceStore store;
    private volatile boolean failed;
    public MobileTrace(Context context, FieldTraceStore.Domain domain) {
        this.context = context.getApplicationContext();
        writer.execute(() -> { try { store = open(this.context); store.begin(domain, System.currentTimeMillis()); }
            catch (IOException e) { failed = true; } });
        record(FieldTraceStore.Event.SESSION_STARTED, null, null, 0);
    }
    public static FieldTraceStore open(Context context) throws IOException { return new FieldTraceStore(new java.io.File(context.getFilesDir(), "aima-field-traces").toPath()); }
    public boolean failed() { return failed; }
    public void flush(Runnable completedOnMain) {
        Handler main = new Handler(Looper.getMainLooper());
        try { writer.execute(() -> main.post(completedOnMain)); }
        catch (RejectedExecutionException e) { failed = true; main.post(completedOnMain); }
    }
    public void record(FieldTraceStore.Event event, VehicleController.Action action, AimaCoordinator.Stage stage, int code) {
        int screen = -1;
        try {
            PowerManager power = context.getSystemService(PowerManager.class);
            screen = (power.isInteractive() ? 1 : 0) | (context.getSystemService(KeyguardManager.class).isKeyguardLocked() ? 2 : 0)
                | (power.isIgnoringBatteryOptimizations(context.getPackageName()) ? 4 : 0) | (power.isPowerSaveMode() ? 8 : 0);
        } catch (RuntimeException ignored) {}
        FieldTraceStore.Entry entry = new FieldTraceStore.Entry(System.currentTimeMillis(), SystemClock.elapsedRealtime(), event, action, stage, code, screen, true);
        try { writer.execute(() -> { try { if (store != null && !store.append(entry)) failed = true; } catch (IOException e) { failed = true; } }); }
        catch (RejectedExecutionException ignored) { failed = true; }
    }
    @Override public void close() { record(FieldTraceStore.Event.SERVICE_STOPPED, null, null, 0); writer.shutdown(); }
}
