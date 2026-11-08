package dev.bandkey.aimakey.core;

/** Entirely in-memory: no Bluetooth, Intent, HTTP, credentials, or real vehicle identifiers. */
public final class MockVehicleController implements VehicleController {
    public enum Scenario { NORMAL, DISCONNECTED, MOVING, UNKNOWN_MOTION, NO_ACK, ACK_ONLY, FAILURE }
    public interface Scheduler { void after(long milliseconds, Runnable action); }
    private final AimaCoordinator.Clock clock;
    private final Scheduler scheduler;
    private Scenario scenario = Scenario.NORMAL;
    private Power power = Power.OFF;
    private int invocations;
    public MockVehicleController(AimaCoordinator.Clock clock, Scheduler scheduler) { this.clock = clock; this.scheduler = scheduler; }
    public synchronized void setScenario(Scenario value) { scenario = value; }
    public synchronized int invocations() { return invocations; }
    @Override public synchronized Snapshot getVehicleConnectionState() {
        return new Snapshot(scenario == Scenario.DISCONNECTED ? Connection.DISCONNECTED : Connection.CONNECTED,
            power, scenario != Scenario.MOVING && scenario != Scenario.UNKNOWN_MOTION, clock.elapsedMs());
    }
    @Override public boolean isSimulation() { return true; }
    @Override public void startVehicle(Callback callback) { execute(Action.START, callback); }
    @Override public void stopVehicle(Callback callback) { execute(Action.STOP, callback); }
    private synchronized void execute(Action action, Callback callback) {
        invocations++; Scenario selected = scenario;
        callback.accept(new Update(Evidence.REQUEST_SENT, null, "MOCK_SENT"));
        if (selected == Scenario.NO_ACK) return;
        scheduler.after(150, () -> callback.accept(new Update(Evidence.VEHICLE_RECEIVED, null, "MOCK_ACK")));
        if (selected == Scenario.ACK_ONLY) return;
        scheduler.after(350, () -> {
            synchronized (this) {
                if (selected == Scenario.FAILURE || scenario == Scenario.DISCONNECTED) {
                    callback.accept(new Update(Evidence.FAILED, null, "MOCK_FAILURE")); return;
                }
                power = action == Action.START ? Power.ON : Power.OFF;
                callback.accept(new Update(Evidence.CONFIRMED, getVehicleConnectionState(), "MOCK_STATE"));
            }
        });
    }
}
