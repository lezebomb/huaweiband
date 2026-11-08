package dev.bandkey.aimakey.core;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Objects;
import java.util.function.Consumer;
import static dev.bandkey.aimakey.core.VehicleController.*;

/** Single explicit request -> at most one adapter invocation. Never retries vehicle operations. */
public final class AimaCoordinator {
    public interface Clock { long wallMs(); long elapsedMs(); }
    /** Must durably commit before returning true; failure prevents any adapter invocation. */
    public interface Journal { boolean reserve(AimaProtocol.Request request); }
    public enum Stage { REJECTED, ACCEPTED, REQUEST_SENT, VEHICLE_RECEIVED, CONFIRMED, FAILED, TIMED_OUT }
    public record Result(String id, Action action, Stage stage, String code, boolean simulation) {}
    private record Entry(AimaProtocol.Request request, Result result) {}
    private final String session;
    private final VehicleController controller;
    private final Clock clock;
    private final Journal journal;
    private final Consumer<Result> listener;
    private final Map<String, Entry> history = new LinkedHashMap<>();
    private AimaProtocol.Request active;
    private long deadlineElapsed, lastSequence;
    private Result last;
    public AimaCoordinator(String session, VehicleController controller, Clock clock, Journal journal, Consumer<Result> listener) {
        if (!session.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("SESSION");
        this.session = session; this.controller = controller; this.clock = clock;
        this.journal = journal; this.listener = listener;
    }
    public String session() { return session; }
    public synchronized Result getLastOperationResult() { return last; }
    public synchronized Result submit(AimaProtocol.Request request) {
        tick();
        if (request == null) return reject(null, "INVALID_REQUEST");
        try { AimaProtocol.encode(request); } catch (IllegalArgumentException e) { return reject(request, "INVALID_REQUEST"); }
        if (!session.equals(request.session())) return reject(request, "SESSION_EXPIRED");
        Entry previous = history.get(request.id());
        if (previous != null) return previous.request().equals(request) ? previous.result() : reject(request, "ID_CONFLICT");
        if (request.sequence() <= lastSequence) return reject(request, "REPLAY");
        // Consume even refused requests: reconnect, a cleared BUSY state or a repeated packet
        // must never turn a previously refused gesture into a later vehicle operation.
        if (history.size() >= 512) return reject(request, "SESSION_FULL");
        lastSequence = request.sequence();
        boolean durable;
        try { durable = journal.reserve(request); } catch (RuntimeException e) { durable = false; }
        if (!durable) return rememberReject(request, "JOURNAL_FAILED");
        long now = clock.wallMs();
        if (request.issuedAtMs() > now || request.expiresAtMs() <= now || now - request.issuedAtMs() > AimaProtocol.MAX_REQUEST_MS)
            return rememberReject(request, "EXPIRED");
        if (active != null) return rememberReject(request, "BUSY");
        Snapshot snapshot;
        try { snapshot = controller.getVehicleConnectionState(); }
        catch (RuntimeException e) { return rememberReject(request, "STATE_UNAVAILABLE"); }
        if (snapshot == null || snapshot.connection() != Connection.CONNECTED) return rememberReject(request, "VEHICLE_NOT_CONNECTED");
        if (!fresh(snapshot)) return rememberReject(request, "STALE_STATE");
        if (request.action() == Action.STOP && (!request.stopConfirmed() || !snapshot.stationaryVerified()))
            return rememberReject(request, request.stopConfirmed() ? "STATIONARY_UNVERIFIED" : "STOP_CONFIRMATION_REQUIRED");
        active = request;
        deadlineElapsed = clock.elapsedMs() + request.expiresAtMs() - now;
        // Reservation is not evidence that a request was sent to the vehicle.
        Result reserved = new Result(request.id(), request.action(), Stage.ACCEPTED, "ADAPTER_DISPATCH_PENDING", controller.isSimulation());
        history.put(request.id(), new Entry(request, reserved));
        try {
            Callback callback = update -> onUpdate(request, update);
            if (request.action() == Action.START) controller.startVehicle(callback); else controller.stopVehicle(callback);
        } catch (RuntimeException e) { finish(request, Stage.FAILED, "ADAPTER_ERROR"); }
        return history.get(request.id()).result();
    }
    private boolean fresh(Snapshot s) {
        long age = clock.elapsedMs() - s.observedElapsedMs(); return age >= 0 && age <= 2_000;
    }
    private synchronized void onUpdate(AimaProtocol.Request request, Update update) {
        tick();
        if (!Objects.equals(active, request) || update == null) return;
        if (update.evidence() == Evidence.CONFIRMED) {
            Snapshot s = update.snapshot();
            Power expected = request.action() == Action.START ? Power.ON : Power.OFF;
            if (s == null || !fresh(s) || s.connection() != Connection.CONNECTED || s.power() != expected)
                finish(request, Stage.FAILED, "CONFIRMATION_UNVERIFIED");
            else finish(request, Stage.CONFIRMED, "VEHICLE_STATE_CONFIRMED");
        } else if (update.evidence() == Evidence.FAILED) finish(request, Stage.FAILED, "ADAPTER_REJECTED");
        else if (update.evidence() == Evidence.REQUEST_SENT || update.evidence() == Evidence.VEHICLE_RECEIVED) {
            Stage stage = update.evidence() == Evidence.REQUEST_SENT ? Stage.REQUEST_SENT : Stage.VEHICLE_RECEIVED;
            Stage previous = history.get(request.id()).result().stage();
            if (previous == Stage.VEHICLE_RECEIVED && stage == Stage.REQUEST_SENT) return;
            publish(request, stage, stage.name());
        }
    }
    public synchronized void tick() {
        if (active != null && clock.elapsedMs() >= deadlineElapsed) finish(active, Stage.TIMED_OUT, "OUTCOME_UNKNOWN_NO_RETRY");
    }
    public synchronized void disconnect() {
        if (active != null) finish(active, Stage.FAILED, "CONNECTION_LOST_OUTCOME_UNKNOWN");
    }
    private void finish(AimaProtocol.Request request, Stage stage, String code) {
        active = null; publish(request, stage, code);
    }
    private void publish(AimaProtocol.Request request, Stage stage, String code) {
        last = new Result(request.id(), request.action(), stage, code, controller.isSimulation());
        history.put(request.id(), new Entry(request, last));
        try { listener.accept(last); } catch (RuntimeException ignored) { /* UI cannot cause an adapter retry. */ }
    }
    private Result reject(AimaProtocol.Request r, String code) {
        return new Result(r == null ? "" : r.id(), r == null ? null : r.action(), Stage.REJECTED, code, controller.isSimulation());
    }
    private Result rememberReject(AimaProtocol.Request request, String code) {
        Result result = reject(request, code); history.put(request.id(), new Entry(request, result)); last = result; return result;
    }
}
