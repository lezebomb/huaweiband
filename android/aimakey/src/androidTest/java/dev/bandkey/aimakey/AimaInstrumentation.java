package dev.bandkey.aimakey;

import android.app.*;
import android.content.*;
import android.os.*;
import dev.bandkey.aimakey.core.*;
import java.util.concurrent.*;

/** Runs only the shipped mock adapter; never starts or instruments the official vehicle app. */
public final class AimaInstrumentation extends Instrumentation {
    private boolean screenOff;
    private AimaService service;
    private AimaActivity activity;
    private int passed;
    @Override public void onCreate(Bundle arguments) { super.onCreate(arguments); screenOff = "true".equals(arguments.getString("screenOff")); start(); }
    private void verify(boolean condition, String message) { if (!condition) throw new AssertionError(message); passed++; }
    @Override public void onStart() {
        Bundle report = new Bundle(); CountDownLatch bound = new CountDownLatch(1);
        ServiceConnection connection = new ServiceConnection() {
            public void onServiceConnected(ComponentName name, IBinder binder) { service = ((AimaService.LocalBinder) binder).service(); bound.countDown(); }
            public void onServiceDisconnected(ComponentName name) { service = null; }
        };
        boolean didBind = false;
        int exitCode = Activity.RESULT_CANCELED;
        try {
            Context context = getTargetContext();
            activity = (AimaActivity) startActivitySync(new Intent(context, AimaActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            didBind = context.bindService(new Intent(context, AimaService.class), connection, Context.BIND_AUTO_CREATE);
            verify(bound.await(8, TimeUnit.SECONDS), "local binder connected");
            verify(service.invocations() == 0, "opening page never sends an action");
            runOnMainSync(() -> service.simulate(VehicleController.Action.START, false)); Thread.sleep(700);
            verify(service.lastResult().stage() == AimaCoordinator.Stage.CONFIRMED && service.lastResult().simulation(), "mock start confirmed with simulation label");
            int before = service.invocations();
            runOnMainSync(() -> service.simulate(VehicleController.Action.STOP, false));
            verify(service.invocations() == before, "unconfirmed stop refused");
            runOnMainSync(() -> service.simulate(VehicleController.Action.STOP, true)); Thread.sleep(700);
            verify(service.snapshot().power() == VehicleController.Power.OFF, "mock stop separate from start");
            runOnMainSync(() -> service.setScenario(MockVehicleController.Scenario.MOVING));
            before = service.invocations(); runOnMainSync(() -> service.simulate(VehicleController.Action.STOP, true));
            verify(service.invocations() == before, "moving mock refuses stop");
            runOnMainSync(() -> { service.setScenario(MockVehicleController.Scenario.NO_ACK); service.simulate(VehicleController.Action.START, false); });
            Thread.sleep(5_300);
            verify(service.lastResult().stage() == AimaCoordinator.Stage.TIMED_OUT, "mock timeout");
            before = service.invocations(); Thread.sleep(500); verify(service.invocations() == before, "timeout has no automatic retry");
            runOnMainSync(() -> { context.startForegroundService(new Intent(context, AimaService.class)); activity.moveTaskToBack(true); });
            Thread.sleep(700); verify(service.isForeground(), "foreground service running with page in background");
            if (screenOff) {
                try (ParcelFileDescriptor fd = getUiAutomation().executeShellCommand("input keyevent 223")) { Thread.sleep(1_500); }
                verify(!context.getSystemService(PowerManager.class).isInteractive(), "physical phone screen is off");
                report.putBoolean("keyguardLocked", context.getSystemService(KeyguardManager.class).isKeyguardLocked());
            }
            runOnMainSync(() -> { service.setScenario(MockVehicleController.Scenario.NORMAL); service.simulate(VehicleController.Action.START, false); });
            Thread.sleep(1_000);
            verify(service.lastResult().stage() == AimaCoordinator.Stage.CONFIRMED && service.lastResult().simulation(), "mock callback in background");
            before = service.invocations();
            runOnMainSync(() -> { service.setScenario(MockVehicleController.Scenario.DISCONNECTED); service.setScenario(MockVehicleController.Scenario.NORMAL); });
            Thread.sleep(500); verify(service.invocations() == before, "reconnect has no implicit action");
            report.putInt("passed", passed); report.putString("scope", "physical Android; mock vehicle only"); report.putBoolean("screenOffTest", screenOff);
            exitCode = Activity.RESULT_OK;
        } catch (Throwable e) { report.putString("failure", e.toString()); report.putInt("passed", passed); }
        finally {
            runOnMainSync(() -> { if (service != null) service.stopBackground(); if (activity != null) activity.finish(); });
            if (didBind) getTargetContext().unbindService(connection);
            if (screenOff) try (ParcelFileDescriptor fd = getUiAutomation().executeShellCommand("input keyevent 224")) {} catch (Exception ignored) {}
        }
        finish(exitCode, report);
    }
}
