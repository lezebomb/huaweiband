package dev.bandkey.aimakey.core;

/** An adapter may only report CONFIRMED using a fresh vehicle-originated state report. */
public interface VehicleController {
    enum Action { START, STOP }
    enum Connection { DISCONNECTED, CONNECTING, CONNECTED, UNKNOWN }
    enum Power { OFF, ON, UNKNOWN }
    enum Evidence { REQUEST_SENT, VEHICLE_RECEIVED, CONFIRMED, FAILED }
    record Snapshot(Connection connection, Power power, boolean stationaryVerified, long observedElapsedMs) {}
    record Update(Evidence evidence, Snapshot snapshot, String code) {}
    interface Callback { void accept(Update update); }
    Snapshot getVehicleConnectionState();
    void startVehicle(Callback callback);
    void stopVehicle(Callback callback);
    boolean isSimulation();
}
