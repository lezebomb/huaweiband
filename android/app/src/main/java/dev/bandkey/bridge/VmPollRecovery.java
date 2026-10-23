package dev.bandkey.bridge;

/** Poll availability only. It cannot retain, approve, or retry an authorization. */
public final class VmPollRecovery {
    private final RetrySchedule retries=new RetrySchedule();
    private boolean waitingForLink;
    private boolean needsRecovery=true;
    public boolean ready(long elapsedMillis,boolean linkAvailable){
        if(!linkAvailable){waitingForLink=true;needsRecovery=true;return false;}
        if(waitingForLink){waitingForLink=false;retries.succeeded();}
        return retries.ready(elapsedMillis);
    }
    public void failed(long elapsedMillis){needsRecovery=true;retries.failed(elapsedMillis);}
    /** Called only after a verified HTTPS poll, never on a GATT connection callback. */
    public boolean succeeded(){boolean recovered=needsRecovery;needsRecovery=false;retries.succeeded();return recovered;}
}
