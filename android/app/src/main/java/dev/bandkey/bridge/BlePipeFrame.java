package dev.bandkey.bridge;

import java.nio.*;
import java.util.Arrays;

/** Same v1 transport framing as C#; it grants no authentication. */
public final class BlePipeFrame {
    public static final int OPEN=1,READY=2,DATA=3,EOF=4,ABORT=5,HEADER=13,MAX_PAYLOAD=160,MAX_BYTES=131072;
    public final int kind,sequence;public final long session;public final byte[] payload;
    public BlePipeFrame(int kind,long session,int sequence,byte[] payload){this.kind=kind;this.session=session;this.sequence=sequence;this.payload=payload.clone();validate();}
    private void validate(){if(kind<OPEN || kind>ABORT || session==0 || sequence<0 || sequence>=65536 || payload.length>MAX_PAYLOAD || (kind==DATA?payload.length==0:payload.length!=0))throw new IllegalArgumentException("BLE frame");}
    public byte[] encode(){validate();return ByteBuffer.allocate(HEADER+payload.length).order(ByteOrder.LITTLE_ENDIAN).put((byte)kind).putLong(session).putInt(sequence).put(payload).array();}
    public static BlePipeFrame decode(byte[] bytes){if(bytes.length<HEADER || bytes.length>HEADER+MAX_PAYLOAD)throw new IllegalArgumentException("BLE length");ByteBuffer input=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);return new BlePipeFrame(input.get()&255,input.getLong(),input.getInt(),Arrays.copyOfRange(bytes,HEADER,bytes.length));}
    public static final class Receiver {
        private final long session;private final int first;private int next,bytes;private boolean ended;
        public Receiver(long session,int first){this.session=session;this.first=first;}
        public synchronized void accept(BlePipeFrame frame){frame.validate();if(ended || frame.session!=session || frame.sequence!=next || (next==0?frame.kind!=first:frame.kind!=DATA && frame.kind!=EOF && frame.kind!=ABORT))throw new IllegalArgumentException("BLE lifecycle");if(bytes+frame.payload.length>MAX_BYTES)throw new IllegalArgumentException("BLE stream limit");bytes+=frame.payload.length;next++;ended=frame.kind==EOF || frame.kind==ABORT;}
    }
}
