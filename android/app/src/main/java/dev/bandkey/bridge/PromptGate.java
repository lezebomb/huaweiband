package dev.bandkey.bridge;

import java.util.function.LongSupplier;

/** Lifecycle and deadline checks shared by polling and asynchronous Wear Engine callbacks. */
public final class PromptGate {
    public static final class Ticket {
        private final long expiresAt, elapsedDeadline;
        private boolean consumed;
        private Ticket(long expiresAt,long elapsedDeadline){this.expiresAt=expiresAt;this.elapsedDeadline=elapsedDeadline;}
    }
    private final LongSupplier wallSeconds,elapsedMillis;
    private boolean stopped;
    private String lastNonce="";
    private Ticket active;

    public PromptGate(LongSupplier wallSeconds,LongSupplier elapsedMillis){
        this.wallSeconds=wallSeconds;this.elapsedMillis=elapsedMillis;
    }
    public synchronized Ticket begin(String nonce,long expiresAt){return begin(nonce,expiresAt,30);}
    public synchronized Ticket begin(String nonce,long expiresAt,long maximumSeconds){
        long remaining=expiresAt-wallSeconds.getAsLong();
        if((maximumSeconds!=30 && maximumSeconds!=120) || stopped || nonce==null || nonce.isEmpty() || nonce.equals(lastNonce) || remaining<=0 || remaining>maximumSeconds)return null;
        lastNonce=nonce;
        active=new Ticket(expiresAt,elapsedMillis.getAsLong()+remaining*1000);
        return active;
    }
    public synchronized boolean isActive(Ticket ticket){
        return current(ticket) && !ticket.consumed;
    }
    private boolean current(Ticket ticket){
        return !stopped && ticket!=null && active==ticket &&
            wallSeconds.getAsLong()<ticket.expiresAt && elapsedMillis.getAsLong()<ticket.elapsedDeadline;
    }
    // Serialize the last check and SDK submission against stop()/replacement. Already submitted
    // notifications cannot be recalled here; their later callbacks still fail these checks.
    public synchronized boolean runIfActive(Ticket ticket,Runnable action){
        if(!isActive(ticket))return false;
        action.run();return true;
    }
    public synchronized boolean consume(Ticket ticket){
        if(!isActive(ticket))return false;
        ticket.consumed=true;return true;
    }
    public synchronized boolean canSubmit(Ticket ticket){return current(ticket) && ticket.consumed;}
    public synchronized void cancel(Ticket ticket){if(active==ticket)active=null;}
    public synchronized void stop(){stopped=true;active=null;}
}
