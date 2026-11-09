package dev.bandkey.aimakey.core;

import java.io.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.*;

public final class AimaFieldTests {
    static int passed;
    static void check(String label, boolean value) { if (!value) throw new AssertionError(label); passed++; }
    public static void main(String[] args) throws Exception {
        WristDiagnosticSession session = new WristDiagnosticSession();
        check("idle cannot accept unsolicited click", session.consume(0, 2, 0) == null);
        long ticket = session.begin(100, 120_000);
        check("sdk delivery cannot approve a gesture", session.state() == WristDiagnosticSession.State.WAITING);
        check("notification dismissal is not start", session.consume(ticket, 0, 200) == null);
        check("unknown code is not a gesture", session.consume(ticket, 99, 200) == null);
        check("start has explicit mapping", session.consume(ticket, 2, 201) == VehicleController.Action.START);
        check("one shot rejects duplicate", session.consume(ticket, 2, 202) == null);
        check("one shot rejects changed action", session.consume(ticket, 3, 203) == null);
        long next = session.begin(300, 1_000);
        check("replacement invalidates old SDK callback", session.consume(ticket, 2, 301) == null);
        check("stop distinct from start", session.consume(next, 3, 302) == VehicleController.Action.STOP);
        next = session.begin(500, 1_000);
        check("deadline boundary rejects late click", session.consume(next, 2, 1_500) == null && session.state() == WristDiagnosticSession.State.EXPIRED);
        check("checking expiry cannot renew", !session.active(next, 1_501));
        next = session.begin(2_000, 1_000); session.cancel(); check("cancel invalidates callbacks", session.consume(next, 2, 2_001) == null);
        next = session.begin(3_000, 1_000); session.fail(next); check("sdk failure prevents acceptance", session.consume(next, 2, 3_001) == null);
        try { session.begin(0, 120_001); throw new AssertionError("unbounded lifetime"); } catch (IllegalArgumentException e) { passed++; }
        try { session.begin(Long.MAX_VALUE, 1_000); throw new AssertionError("overflow"); } catch (IllegalArgumentException e) { passed++; }
        Path directory = Files.createTempDirectory(Path.of("artifacts/aimakey"), "field-store-");
        FieldTraceStore store = new FieldTraceStore(directory);
        check("new store empty", store.count() == 0 && store.latest().bytes().length == 0);
        store.begin(FieldTraceStore.Domain.WRIST_NOTIFICATION_TEST, 1_000_000);
        store.append(new FieldTraceStore.Entry(1_000_001, 30, FieldTraceStore.Event.SDK_ACCEPTED, null, null, 1, 2, true));
        store.append(new FieldTraceStore.Entry(1_000_002, 31, FieldTraceStore.Event.WRIST_GESTURE, VehicleController.Action.STOP, null, 3, 2, true));
        String content = new String(store.latest().bytes(), StandardCharsets.UTF_8);
        check("wrist source is explicit", content.contains("WRIST_NOTIFICATION_TEST") && content.contains("WRIST_GESTURE"));
        check("receipt separate from SDK acceptance", content.contains("SDK_ACCEPTED") && content.contains("\"action\":\"STOP\""));
        check("locked phone flag persisted", content.contains("\"screenFlags\":2"));
        check("log excludes raw identifiers by construction", !content.contains("requestId") && !content.contains("session") && !content.contains("bluetooth") && !content.contains("token"));
        check("log cannot claim live vehicle adapter", content.contains("\"realVehicleAdapter\":false"));
        check("real wrist event distinct from simulated vehicle", content.contains("WRIST_GESTURE") && content.contains("\"vehicleSimulation\":true"));
        check("records survive new reader", Arrays.equals(store.latest().bytes(), new FieldTraceStore(directory).latest().bytes()));
        Path file; try (var paths = Files.list(directory)) { file = paths.findFirst().orElseThrow(); }
        Files.writeString(file, "{partial", StandardOpenOption.APPEND);
        check("torn trailing row excluded", store.latest().discardedIncompleteTail() && Arrays.equals(store.latest().bytes(), content.getBytes(StandardCharsets.UTF_8)));
        ByteArrayOutputStream bytes = new ByteArrayOutputStream(); store.exportZip(bytes);
        Map<String, String> exported = new HashMap<>(); try (ZipInputStream zip = new ZipInputStream(new ByteArrayInputStream(bytes.toByteArray()))) {
            ZipEntry entry; while ((entry = zip.getNextEntry()) != null) exported.put(entry.getName(), new String(zip.readAllBytes(), StandardCharsets.UTF_8));
        }
        check("portable report without host paths", exported.keySet().equals(Set.of("manifest.json", "trace-1.jsonl")) && !exported.toString().contains(directory.toString()));
        check("partial tail explicitly reported", exported.get("manifest.json").contains("\"incompleteTailsDiscarded\":1"));
        check("export not replayable request", !exported.get("trace-1.jsonl").contains("aimakey/1"));
        Files.writeString(directory.resolve("unrelated-private.txt"), "preserve");
        for (int i = 0; i < 12; i++) { store.begin(FieldTraceStore.Domain.PHONE_MOCK, 100); store.append(new FieldTraceStore.Entry(100, i, FieldTraceStore.Event.SESSION_STARTED, null, null, 0, 1, true)); }
        check("bounded retention across clock rollback", store.count() == FieldTraceStore.MAX_FILES);
        check("rotation preserves unrelated app files", Files.readString(directory.resolve("unrelated-private.txt")).equals("preserve"));
        check("newest session readable despite clock rollback", new String(store.latest().bytes(), StandardCharsets.UTF_8).contains("\"elapsedMs\":11"));
        int accepted = 0; while (store.append(new FieldTraceStore.Entry(100, accepted, FieldTraceStore.Event.SDK_QUERY, null, null, 0, 0, true))) accepted++;
        check("per file byte bound", store.latest().bytes().length <= FieldTraceStore.MAX_BYTES && accepted > 0);
        check("storage overflow recorded", new String(store.latest().bytes(), StandardCharsets.UTF_8).endsWith("{\"event\":\"STORAGE_LIMIT\"}\n"));
        check("full log does not grow", !store.append(new FieldTraceStore.Entry(100, 100, FieldTraceStore.Event.SDK_QUERY, null, null, 0, 0, true)));
        String report = "{\"suite\":\"aimakey-field\",\"passed\":" + passed + ",\"failed\":0,\"scope\":\"local log and one-shot policy; no live SDK feedback\"}";
        Files.writeString(Path.of(args[0]), report); System.out.println("AimaKey field: " + passed + " checks passed");
    }
}
