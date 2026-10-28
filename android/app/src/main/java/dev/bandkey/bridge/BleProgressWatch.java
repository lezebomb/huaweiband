package dev.bandkey.bridge;

/** Availability watchdog only. A verified response is progress, never approval. */
final class BleProgressWatch {
    private long lastNow=-1,lastProgress,lastCompleted;
    boolean restartDue(long now,long completed){
        if(now<0 || completed<0 || now<lastNow || lastNow>=0 && completed<lastCompleted)throw new IllegalArgumentException("Monotonic progress required");
        if(lastNow<0 || completed>lastCompleted)lastProgress=now;
        lastNow=now;lastCompleted=completed;
        if(now-lastProgress<45000)return false;
        lastProgress=now;return true;
    }
}
