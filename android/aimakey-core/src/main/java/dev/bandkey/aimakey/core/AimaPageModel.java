package dev.bandkey.aimakey.core;

/** Transport-independent wrist-page state, usable by a future verified native wearable app. */
public final class AimaPageModel {
    public interface Link { void sendExplicitGesture(byte[] request); }
    private final Link link;
    private String session;
    private boolean connected;
    private boolean simulation;
    private long sequence;
    private AimaProtocol.Request pending;
    private AimaCoordinator.Result result;
    public AimaPageModel(Link link) { this.link = link; }
    public synchronized void connected(String newSession, boolean isSimulation) {
        if (!newSession.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("SESSION");
        // Reconnection only updates UI/session state. It never repeats a pending gesture.
        if (!newSession.equals(session)) { sequence = 0; result = null; }
        session = newSession; connected = true; simulation = isSimulation; pending = null;
    }
    public synchronized void disconnected() {
        if (pending != null) result = new AimaCoordinator.Result(pending.id(), pending.action(), AimaCoordinator.Stage.FAILED, "CONNECTION_LOST_OUTCOME_UNKNOWN", simulation);
        connected = false; pending = null;
    }
    public synchronized String activate(VehicleController.Action action, boolean stopConfirmed, long nowMs) {
        if (!connected) return "PHONE_NOT_CONNECTED";
        if (pending != null) return "BUSY";
        if (action == VehicleController.Action.STOP && !stopConfirmed) return "STOP_CONFIRMATION_REQUIRED";
        pending = new AimaProtocol.Request(session, AimaProtocol.newId(), ++sequence, action, nowMs, nowMs + 5_000, stopConfirmed);
        result = null;
        try { link.sendExplicitGesture(AimaProtocol.encode(pending)); }
        catch (RuntimeException e) { pending = null; return "TRANSPORT_FAILED_OUTCOME_UNKNOWN"; }
        return "REQUESTED";
    }
    public synchronized void receive(byte[] bytes) {
        AimaProtocol.Reply reply = AimaProtocol.decodeReply(bytes);
        if (!connected || !session.equals(reply.session()) || pending == null
            || !pending.id().equals(reply.result().id()) || pending.action() != reply.result().action()) return;
        if (result != null && result.stage() == AimaCoordinator.Stage.VEHICLE_RECEIVED
            && (reply.result().stage() == AimaCoordinator.Stage.REQUEST_SENT || reply.result().stage() == AimaCoordinator.Stage.ACCEPTED)) return;
        result = reply.result();
        if (switch (result.stage()) { case REJECTED, CONFIRMED, FAILED, TIMED_OUT -> true; default -> false; }) pending = null;
    }
    public synchronized void tick(long nowMs) { if (pending != null && nowMs >= pending.expiresAtMs()) {
        result = new AimaCoordinator.Result(pending.id(), pending.action(), AimaCoordinator.Stage.TIMED_OUT, "OUTCOME_UNKNOWN_NO_RETRY", simulation);
        pending = null;
    } }
    public synchronized AimaCoordinator.Result result() { return result; }
    public synchronized boolean busy() { return pending != null; }
}
