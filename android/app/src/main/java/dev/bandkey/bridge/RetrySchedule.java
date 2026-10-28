package dev.bandkey.bridge;

/** Bounded monotonic retry schedule; a successful poll resets the failure sequence. */
public final class RetrySchedule {
    private final long baseDelay,maxDelay;
    public RetrySchedule(){this(2000,60000);}
    public RetrySchedule(long baseDelay,long maxDelay){
        if(baseDelay<1 || baseDelay>60000 || maxDelay<baseDelay*2 || maxDelay>60000)throw new IllegalArgumentException("Retry delay");
        this.baseDelay=baseDelay;this.maxDelay=maxDelay;
    }
    private int failures;
    private long nextAttempt;
    public boolean ready(long elapsedMillis){return elapsedMillis>=nextAttempt;}
    public void failed(long elapsedMillis){
        failures=Math.min(failures+1,5);
        nextAttempt=elapsedMillis+Math.min(maxDelay,baseDelay*(1L<<failures));
    }
    public void succeeded(){failures=0;nextAttempt=0;}
}
