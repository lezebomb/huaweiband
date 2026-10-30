package dev.bandkey.bridge;

/** A late SDK callback cannot overwrite a newer connection sample. */
final class WearQueryWatch {
    static final long TIMEOUT_MS=5000;
    private long generation,active,started;
    synchronized long begin(long now){if(active!=0)throw new IllegalStateException("Query pending");started=now;return active=++generation;}
    synchronized boolean complete(long id,long now){
        if(active!=id || id==0 || now<started || now-started>=TIMEOUT_MS)return false;
        active=0;return true;
    }
    synchronized boolean expire(long id,long now){
        if(active!=id || id==0 || now>=started && now-started<TIMEOUT_MS)return false;
        active=0;return true;
    }
    synchronized void cancel(){active=0;}
}
