package dev.bandkey.aimakey.core;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;

/** Typed, bounded app-private evidence. Never accepts packet bodies, device IDs, tokens or free text. */
public final class FieldTraceStore {
    public enum Domain { PHONE_MOCK, WRIST_NOTIFICATION_TEST }
    public enum Event { SESSION_STARTED, SERVICE_STARTED, SERVICE_STOPPED, PHONE_GESTURE, WRIST_GESTURE,
        SDK_QUERY, SDK_CONNECTED, SDK_DISCONNECTED, SDK_ACCEPTED, SDK_FEEDBACK, SDK_ERROR, REQUEST_RESULT,
        EXPIRED, CANCELLED, HOST_BUSY, STORAGE_LIMIT }
    public record Entry(long wallMs, long elapsedMs, Event event, VehicleController.Action action,
                        AimaCoordinator.Stage stage, int code, int screenFlags, boolean vehicleSimulation) {
        public Entry { if (wallMs < 0 || elapsedMs < 0 || event == null || screenFlags < -1 || screenFlags > 15) throw new IllegalArgumentException("TRACE_FIELDS"); }
    }
    public record Snapshot(byte[] bytes, boolean discardedIncompleteTail) {
        public Snapshot { bytes = bytes.clone(); }
        @Override public byte[] bytes() { return bytes.clone(); }
    }
    public static final int MAX_FILES = 8, MAX_BYTES = 131_072;
    private final Path directory;
    private Path current;
    private int sequence;
    private boolean full;
    public FieldTraceStore(Path directory) throws IOException {
        this.directory = directory.toAbsolutePath().normalize();
        if (Files.isSymbolicLink(this.directory)) throw new IOException("TRACE_DIRECTORY_LINK");
        Files.createDirectories(this.directory);
    }
    public synchronized void begin(Domain domain, long now) throws IOException {
        if (domain == null || now < 0 || now > 9_999_999_999_999_999L) throw new IllegalArgumentException("TRACE_SESSION");
        List<Path> previous = files();
        for (Path path : previous) now = Math.max(now, Long.parseLong(path.getFileName().toString().split("-")[1]) + 1);
        current = directory.resolve("trace-" + now + "-" + UUID.randomUUID() + ".jsonl"); sequence = 0; full = false;
        Files.write(current, ("{\"schema\":\"aimakey.field-trace.v1\",\"domain\":\"" + domain + "\",\"realVehicleAdapter\":false}\n").getBytes(StandardCharsets.UTF_8), StandardOpenOption.CREATE_NEW);
        while (previous.size() + 1 > MAX_FILES) Files.delete(previous.remove(0));
    }
    public synchronized boolean append(Entry entry) throws IOException {
        if (current == null) throw new IllegalStateException("TRACE_NOT_STARTED");
        if (full) return false;
        String line = "{\"seq\":" + (sequence + 1) + ",\"wallMs\":" + entry.wallMs() + ",\"elapsedMs\":" + entry.elapsedMs()
            + ",\"event\":\"" + entry.event() + "\",\"action\":\"" + (entry.action() == null ? "NONE" : entry.action())
            + "\",\"stage\":\"" + (entry.stage() == null ? "NONE" : entry.stage()) + "\",\"code\":" + entry.code()
            + ",\"screenFlags\":" + entry.screenFlags() + ",\"vehicleSimulation\":" + entry.vehicleSimulation() + "}\n";
        byte[] bytes = line.getBytes(StandardCharsets.UTF_8);
        if (Files.size(current) + bytes.length + 64 > MAX_BYTES) {
            write("{\"event\":\"STORAGE_LIMIT\"}\n".getBytes(StandardCharsets.UTF_8)); full = true; return false;
        }
        write(bytes); sequence++; return true;
    }
    private void write(byte[] bytes) throws IOException {
        if (Files.isSymbolicLink(current)) throw new IOException("TRACE_FILE_LINK");
        try (FileOutputStream stream = new FileOutputStream(current.toFile(), true)) { stream.write(bytes); stream.getFD().sync(); }
    }
    private List<Path> files() throws IOException {
        List<Path> result = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(directory, "trace-*.jsonl")) {
            for (Path path : stream) if (path.getFileName().toString().matches("trace-[0-9]{1,16}-[a-f0-9-]{36}\\.jsonl")
                && Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS)) result.add(path);
        }
        result.sort(Comparator.comparing(path -> path.getFileName().toString())); return result;
    }
    public synchronized int count() throws IOException { return files().size(); }
    public synchronized Snapshot latest() throws IOException {
        List<Path> result = files(); if (result.isEmpty()) return new Snapshot(new byte[0], false);
        return snapshot(result.get(result.size() - 1));
    }
    private Snapshot snapshot(Path file) throws IOException {
        if (Files.size(file) > MAX_BYTES || Files.isSymbolicLink(file)) throw new IOException("TRACE_SIZE_OR_LINK");
        byte[] bytes = Files.readAllBytes(file); int end = bytes.length;
        while (end > 0 && bytes[end - 1] != '\n') end--;
        return new Snapshot(Arrays.copyOf(bytes, end), end != bytes.length);
    }
    public synchronized void exportZip(OutputStream destination) throws IOException {
        List<Path> files = files(); if (files.isEmpty()) throw new IOException("NO_DIAGNOSTICS");
        try (ZipOutputStream zip = new ZipOutputStream(destination, StandardCharsets.UTF_8)) {
            int tails = 0, index = 0;
            for (Path file : files) {
                Snapshot snapshot = snapshot(file); if (snapshot.discardedIncompleteTail()) tails++;
                zip.putNextEntry(new ZipEntry("trace-" + (++index) + ".jsonl")); zip.write(snapshot.bytes()); zip.closeEntry();
            }
            zip.putNextEntry(new ZipEntry("manifest.json"));
            zip.write(("{\"schema\":\"aimakey.field-export.v1\",\"files\":" + files.size() + ",\"incompleteTailsDiscarded\":" + tails
                + ",\"scope\":\"own app events only; no official-app logcat\",\"vehicleControlEnabled\":false}").getBytes(StandardCharsets.UTF_8)); zip.closeEntry();
        }
    }
}
