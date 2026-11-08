package dev.bandkey.aimakey.core;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

/** Small, strict, versioned wire contract. Has no BandKey Windows approval messages. */
public final class AimaProtocol {
    private AimaProtocol() {}
    public static final long MAX_REQUEST_MS = 15_000;
    public record Request(String session, String id, long sequence, VehicleController.Action action,
                          long issuedAtMs, long expiresAtMs, boolean stopConfirmed) {}
    public static String newId() { return UUID.randomUUID().toString(); }
    public static byte[] encode(Request r) {
        String wire = "aimakey/1\t" + r.session() + "\t" + r.id() + "\t" + r.sequence() + "\t" + r.action()
            + "\t" + r.issuedAtMs() + "\t" + r.expiresAtMs() + "\t" + (r.stopConfirmed() ? "1" : "0");
        byte[] bytes = wire.getBytes(StandardCharsets.UTF_8);
        decode(bytes); // Encoding enforces exactly the same validation as reception.
        return bytes;
    }
    public static Request decode(byte[] bytes) {
        if (bytes == null || bytes.length == 0 || bytes.length > 256) throw new IllegalArgumentException("SIZE");
        String text;
        try {
            text = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
        } catch (CharacterCodingException e) { throw new IllegalArgumentException("UTF8", e); }
        String[] f = text.split("\t", -1);
        if (f.length != 8 || !"aimakey/1".equals(f[0]) || !f[1].matches("[a-f0-9]{32}")
            || !f[2].matches("[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}")
            || !f[3].matches("[1-9][0-9]{0,17}") || !f[5].matches("[0-9]{1,16}")
            || !f[6].matches("[0-9]{1,16}") || !("0".equals(f[7]) || "1".equals(f[7])))
            throw new IllegalArgumentException("FORMAT");
        try {
            long issued = Long.parseLong(f[5]), expires = Long.parseLong(f[6]);
            if (expires <= issued || expires - issued > MAX_REQUEST_MS) throw new IllegalArgumentException("TTL");
            return new Request(f[1], f[2], Long.parseLong(f[3]), VehicleController.Action.valueOf(f[4]),
                issued, expires, "1".equals(f[7]));
        } catch (NumberFormatException e) { throw new IllegalArgumentException("NUMBER", e); }
    }
    public record Reply(String session, AimaCoordinator.Result result) {}
    public static byte[] encodeReply(String session, AimaCoordinator.Result result) {
        String wire = "aimakey/result/1\t" + session + "\t" + result.id() + "\t" + result.action()
            + "\t" + result.stage() + "\t" + result.code() + "\t" + (result.simulation() ? "1" : "0");
        byte[] bytes = wire.getBytes(StandardCharsets.US_ASCII); decodeReply(bytes); return bytes;
    }
    public static Reply decodeReply(byte[] bytes) {
        if (bytes == null || bytes.length == 0 || bytes.length > 256) throw new IllegalArgumentException("SIZE");
        for (byte b : bytes) if (b < 0 || (b < 32 && b != 9)) throw new IllegalArgumentException("ASCII");
        String[] f = new String(bytes, StandardCharsets.US_ASCII).split("\t", -1);
        if (f.length != 7 || !"aimakey/result/1".equals(f[0]) || !f[1].matches("[a-f0-9]{32}")
            || !f[2].matches("[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}")
            || !f[5].matches("[A-Z_]{1,64}") || !("0".equals(f[6]) || "1".equals(f[6]))) throw new IllegalArgumentException("FORMAT");
        return new Reply(f[1], new AimaCoordinator.Result(f[2], VehicleController.Action.valueOf(f[3]),
            AimaCoordinator.Stage.valueOf(f[4]), f[5], "1".equals(f[6])));
    }
}
