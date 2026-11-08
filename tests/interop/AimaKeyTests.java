package dev.bandkey.aimakey.core;

import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import static dev.bandkey.aimakey.core.VehicleController.*;
import static dev.bandkey.aimakey.core.AimaCoordinator.Stage.*;

public final class AimaKeyTests {
    static final String SESSION = "0123456789abcdef0123456789abcdef";
    static final List<String> passed = new ArrayList<>();
    static final class Time implements AimaCoordinator.Clock {
        long wall = 1_700_000_000_000L, elapsed = 1_000;
        public long wallMs() { return wall; } public long elapsedMs() { return elapsed; }
        void advance(long ms) { wall += ms; elapsed += ms; }
    }
    static final class Rig {
        final Time time = new Time(); final List<Runnable> work = new ArrayList<>();
        final MockVehicleController mock = new MockVehicleController(time, (ms, job) -> work.add(job));
        final List<AimaCoordinator.Result> events = new ArrayList<>();
        final AimaCoordinator coordinator = new AimaCoordinator(SESSION, mock, time, r -> true, events::add);
        long sequence;
        AimaProtocol.Request request(Action action, boolean confirm) {
            return new AimaProtocol.Request(SESSION, AimaProtocol.newId(), ++sequence, action, time.wall, time.wall + 5_000, confirm);
        }
        void complete() { for (Runnable job : new ArrayList<>(work)) job.run(); work.clear(); }
    }
    static void check(String name, boolean condition) { if (!condition) throw new AssertionError(name); passed.add(name); }
    static void malformed(String name, byte[] bytes) {
        try { AimaProtocol.decode(bytes); throw new AssertionError(name); } catch (IllegalArgumentException e) { passed.add(name); }
    }
    public static void main(String[] args) throws Exception {
        Rig r = new Rig(); var start = r.request(Action.START, false);
        check("wire roundtrip", AimaProtocol.decode(AimaProtocol.encode(start)).equals(start));
        malformed("unknown namespace", new String(AimaProtocol.encode(start), StandardCharsets.UTF_8).replace("aimakey/1", "bandkey/1").getBytes(StandardCharsets.UTF_8));
        malformed("unknown action", new String(AimaProtocol.encode(start), StandardCharsets.UTF_8).replace("START", "UNLOCK").getBytes(StandardCharsets.UTF_8));
        malformed("trailing fields", (new String(AimaProtocol.encode(start), StandardCharsets.UTF_8) + "\t1").getBytes(StandardCharsets.UTF_8));
        malformed("invalid utf8", new byte[]{(byte)0xc3, 0x28});
        malformed("oversized packet", new byte[257]); malformed("empty packet", new byte[0]); malformed("null packet", null);
        check("sent is not confirmation", r.coordinator.submit(start).stage() == REQUEST_SENT && r.mock.invocations() == 1);
        check("duplicate while pending does not dispatch", r.coordinator.submit(start).stage() == REQUEST_SENT && r.mock.invocations() == 1);
        var conflict = new AimaProtocol.Request(SESSION, start.id(), start.sequence(), Action.STOP, start.issuedAtMs(), start.expiresAtMs(), true);
        check("same id changed action refused", r.coordinator.submit(conflict).code().equals("ID_CONFLICT"));
        check("busy refuses second action", r.coordinator.submit(r.request(Action.START, false)).code().equals("BUSY"));
        r.complete();
        check("three evidence stages separate", r.events.stream().map(AimaCoordinator.Result::stage).toList().equals(List.of(REQUEST_SENT, VEHICLE_RECEIVED, CONFIRMED)));
        check("mock success always labelled", r.coordinator.getLastOperationResult().simulation());
        check("duplicate terminal does not dispatch", r.coordinator.submit(start).stage() == CONFIRMED && r.mock.invocations() == 1);
        check("stop requires second confirmation", r.coordinator.submit(r.request(Action.STOP, false)).code().equals("STOP_CONFIRMATION_REQUIRED"));
        var stop = r.request(Action.STOP, true); r.coordinator.submit(stop); r.complete();
        check("start stop are distinct", r.mock.getVehicleConnectionState().power() == Power.OFF && r.mock.invocations() == 2);
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.DISCONNECTED);
        check("disconnected refuses dispatch", r.coordinator.submit(r.request(Action.START, false)).code().equals("VEHICLE_NOT_CONNECTED") && r.mock.invocations() == 0);
        r.mock.setScenario(MockVehicleController.Scenario.NORMAL); r.coordinator.tick(); r.complete();
        check("reconnect never starts vehicle", r.mock.invocations() == 0);
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.DISCONNECTED); start = r.request(Action.START, false);
        r.coordinator.submit(start); r.mock.setScenario(MockVehicleController.Scenario.NORMAL);
        check("repeated rejected request stays refused after reconnect", r.coordinator.submit(start).code().equals("VEHICLE_NOT_CONNECTED") && r.mock.invocations() == 0);
        r.mock.setScenario(MockVehicleController.Scenario.MOVING);
        check("moving refuses stop", r.coordinator.submit(r.request(Action.STOP, true)).code().equals("STATIONARY_UNVERIFIED") && r.mock.invocations() == 0);
        r.mock.setScenario(MockVehicleController.Scenario.UNKNOWN_MOTION);
        check("unknown motion refuses stop", r.coordinator.submit(r.request(Action.STOP, true)).code().equals("STATIONARY_UNVERIFIED"));
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.NO_ACK); start = r.request(Action.START, false); r.coordinator.submit(start);
        r.time.advance(5_000); r.coordinator.tick();
        check("timeout outcome unknown", r.coordinator.getLastOperationResult().stage() == TIMED_OUT);
        check("timeout duplicate never retries", r.coordinator.submit(start).stage() == TIMED_OUT && r.mock.invocations() == 1);
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.ACK_ONLY); r.coordinator.submit(r.request(Action.START, false)); r.complete();
        check("received is not success", r.coordinator.getLastOperationResult().stage() == VEHICLE_RECEIVED);
        r.time.advance(5_000); r.coordinator.tick(); check("ack without state times out", r.coordinator.getLastOperationResult().stage() == TIMED_OUT);
        r = new Rig(); r.coordinator.submit(r.request(Action.START, false)); r.coordinator.disconnect(); r.complete();
        check("late callbacks after disconnect ignored", r.coordinator.getLastOperationResult().code().equals("CONNECTION_LOST_OUTCOME_UNKNOWN"));
        check("disconnect does not retry", r.mock.invocations() == 1);
        r = new Rig(); r.coordinator.submit(r.request(Action.START, false)); r.time.advance(5_001); r.complete();
        check("late success after deadline ignored", r.coordinator.getLastOperationResult().stage() == TIMED_OUT);
        r = new Rig(); start = r.request(Action.START, false); r.time.advance(5_000);
        check("expired request refused before dispatch", r.coordinator.submit(start).code().equals("EXPIRED") && r.mock.invocations() == 0);
        r = new Rig(); start = r.request(Action.START, false); r.time.wall -= 1;
        check("future request refused", r.coordinator.submit(start).code().equals("EXPIRED"));
        r = new Rig(); start = r.request(Action.START, false);
        var oldSession = new AimaProtocol.Request("ffffffffffffffffffffffffffffffff", start.id(), start.sequence(), start.action(), start.issuedAtMs(), start.expiresAtMs(), false);
        check("old session refused after restart", r.coordinator.submit(oldSession).code().equals("SESSION_EXPIRED"));
        r.coordinator.submit(start); r.complete();
        var replay = new AimaProtocol.Request(SESSION, AimaProtocol.newId(), start.sequence(), Action.START, start.issuedAtMs(), start.expiresAtMs(), false);
        check("same sequence new id refused", r.coordinator.submit(replay).code().equals("REPLAY"));
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.FAILURE); r.coordinator.submit(r.request(Action.START, false)); r.complete();
        check("adapter failure not success", r.coordinator.getLastOperationResult().stage() == FAILED);
        Time time = new Time(); var mock = new MockVehicleController(time, (ms, job) -> {});
        var failedJournal = new AimaCoordinator(SESSION, mock, time, q -> false, v -> {});
        var req = new AimaProtocol.Request(SESSION, AimaProtocol.newId(), 1, Action.START, time.wall, time.wall + 5_000, false);
        check("storage failure prevents side effect", failedJournal.submit(req).code().equals("JOURNAL_FAILED") && mock.invocations() == 0);
        VehicleController invalid = new VehicleController() {
            public Snapshot getVehicleConnectionState() { return new Snapshot(Connection.CONNECTED, Power.OFF, true, time.elapsed); }
            public boolean isSimulation() { return true; }
            public void startVehicle(Callback cb) { cb.accept(new Update(Evidence.CONFIRMED, getVehicleConnectionState(), "")); }
            public void stopVehicle(Callback cb) { cb.accept(new Update(Evidence.CONFIRMED, null, "")); }
        };
        var unverified = new AimaCoordinator(SESSION, invalid, time, q -> true, v -> {});
        check("wrong power cannot confirm start", unverified.submit(req).code().equals("CONFIRMATION_UNVERIFIED"));
        var nullState = new AimaProtocol.Request(SESSION, AimaProtocol.newId(), 2, Action.STOP, time.wall, time.wall + 5_000, true);
        check("missing state cannot confirm stop", unverified.submit(nullState).code().equals("CONFIRMATION_UNVERIFIED"));
        r = new Rig(); r.mock.setScenario(MockVehicleController.Scenario.NO_ACK); r.coordinator.submit(r.request(Action.START, false)); r.time.wall -= 86_400_000; r.time.elapsed += 5_000; r.coordinator.tick();
        check("wall clock rollback cannot extend deadline", r.coordinator.getLastOperationResult().stage() == TIMED_OUT);
        r = new Rig();
        for (int i = 0; i < 512; i++) { r.coordinator.submit(r.request(Action.START, false)); r.complete(); }
        check("capacity refuses rather than evicting replay history", r.coordinator.submit(r.request(Action.START, false)).code().equals("SESSION_FULL") && r.mock.invocations() == 512);
        List<byte[]> outgoing = new ArrayList<>(); AimaPageModel page = new AimaPageModel(outgoing::add);
        check("page disconnected blocks request", page.activate(Action.START, false, time.wall).equals("PHONE_NOT_CONNECTED"));
        page.connected(SESSION, true);
        check("connecting page has no command", outgoing.isEmpty());
        check("page stop requires confirm", page.activate(Action.STOP, false, time.wall).equals("STOP_CONFIRMATION_REQUIRED") && outgoing.isEmpty());
        page.activate(Action.START, false, time.wall); var pageRequest = AimaProtocol.decode(outgoing.get(0));
        check("page explicit click sends once", outgoing.size() == 1 && page.busy());
        check("page pending blocks extra click", page.activate(Action.START, false, time.wall).equals("BUSY") && outgoing.size() == 1);
        var received = new AimaCoordinator.Result(pageRequest.id(), Action.START, VEHICLE_RECEIVED, "VEHICLE_RECEIVED", true);
        check("reply roundtrip", AimaProtocol.decodeReply(AimaProtocol.encodeReply(SESSION, received)).result().equals(received));
        page.receive(AimaProtocol.encodeReply(SESSION, received));
        check("page ack still busy and not successful", page.busy() && page.result().stage() == VEHICLE_RECEIVED);
        page.receive(AimaProtocol.encodeReply(SESSION, new AimaCoordinator.Result(pageRequest.id(), Action.START, REQUEST_SENT, "REQUEST_SENT", true)));
        check("late delivery cannot regress acknowledgement", page.result().stage() == VEHICLE_RECEIVED);
        page.receive(AimaProtocol.encodeReply(SESSION, new AimaCoordinator.Result(AimaProtocol.newId(), Action.START, CONFIRMED, "VEHICLE_STATE_CONFIRMED", true)));
        check("unrelated result cannot change page", page.result().stage() == VEHICLE_RECEIVED);
        page.receive(AimaProtocol.encodeReply("ffffffffffffffffffffffffffffffff", new AimaCoordinator.Result(pageRequest.id(), Action.START, CONFIRMED, "VEHICLE_STATE_CONFIRMED", true)));
        check("old session result ignored", page.result().stage() == VEHICLE_RECEIVED);
        page.receive(AimaProtocol.encodeReply(SESSION, new AimaCoordinator.Result(pageRequest.id(), Action.START, REJECTED, "VEHICLE_NOT_CONNECTED", true)));
        check("rejection after acknowledgement is terminal", !page.busy() && page.result().stage() == REJECTED);
        page.activate(Action.START, false, time.wall); page.disconnected(); page.connected(SESSION, true);
        check("page reconnect never repeats gesture", outgoing.size() == 2 && !page.busy());
        page.activate(Action.START, false, time.wall);
        check("same session reconnect preserves sequence", AimaProtocol.decode(outgoing.get(2)).sequence() == 3);
        page.tick(time.wall + 5_000);
        check("page local timeout preserves simulation label", !page.busy() && page.result().stage() == TIMED_OUT && page.result().simulation());
        check("page timeout never retransmits", outgoing.size() == 3);
        var badReply = AimaProtocol.encodeReply(SESSION, received); badReply[0] = (byte)0xff;
        try { AimaProtocol.decodeReply(badReply); throw new AssertionError("invalid reply accepted"); } catch (IllegalArgumentException expected) { passed.add("invalid reply encoding refused"); }
        String json = "{\"suite\":\"aimakey-core\",\"passed\":" + passed.size() + ",\"failed\":0,\"scope\":\"simulation only\",\"checks\":[" + String.join(",", passed.stream().map(x -> "\"" + x + "\"").toList()) + "]}";
        Files.writeString(Path.of(args[0]), json); System.out.println("AimaKey: " + passed.size() + " checks passed (mock only)");
    }
}
