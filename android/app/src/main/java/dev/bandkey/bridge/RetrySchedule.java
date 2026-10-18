package dev.bandkey.bridge;

/** Bounded monotonic retry schedule; a successful poll resets the failure sequence. */
public final class RetrySchedule {
    private int failures;
    private long nextAttempt;
    public boolean ready(long elapsedMillis){return elapsedMillis>=nextAttempt;}
    public void failed(long elapsedMillis){
        failures=Math.min(failures+1,5);
        nextAttempt=elapsedMillis+Math.min(60000,2000L*(1L<<failures));
    }
    public void succeeded(){failures=0;nextAttempt=0;}
}
