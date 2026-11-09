package dev.bandkey.aimakey.core;

/** One explicitly requested notification, one gesture, no vehicle adapter and no renewal. */
public final class WristDiagnosticSession {
    public enum State { IDLE, WAITING, START_RECEIVED, STOP_RECEIVED, EXPIRED, CANCELLED, ERROR }
    private long generation, deadline;
    private State state = State.IDLE;
    public synchronized long begin(long elapsed, long lifetimeMs) {
        if (elapsed < 0 || lifetimeMs < 1_000 || lifetimeMs > 120_000 || elapsed > Long.MAX_VALUE - lifetimeMs) throw new IllegalArgumentException("DIAGNOSTIC_LIFETIME");
        generation++; deadline = elapsed + lifetimeMs; state = State.WAITING; return generation;
    }
    public synchronized boolean active(long ticket, long elapsed) {
        if (ticket != generation || elapsed < 0) return false;
        if (state == State.WAITING && elapsed >= deadline) state = State.EXPIRED;
        return state == State.WAITING;
    }
    public synchronized VehicleController.Action consume(long ticket, int feedback, long elapsed) {
        if (!active(ticket, elapsed) || (feedback != 2 && feedback != 3)) return null;
        state = feedback == 2 ? State.START_RECEIVED : State.STOP_RECEIVED;
        return feedback == 2 ? VehicleController.Action.START : VehicleController.Action.STOP;
    }
    public synchronized void cancel() { generation++; state = State.CANCELLED; }
    public synchronized void fail(long ticket) { if (ticket == generation && state == State.WAITING) state = State.ERROR; }
    public synchronized State state() { return state; }
}
