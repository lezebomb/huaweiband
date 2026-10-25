package dev.bandkey.bridge;

import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import java.util.function.LongSupplier;

/** A single explicit VM-only background run. Adapters must verify PC prompts before returning them. */
public final class VmWearSession<P> {
    public static final long MAX_DURATION=30*60*1000L;
    public enum State { WAIT_LINK,WAIT_REQUEST,WAIT_CLICK,QUEUED,DELIVERED,DENIED,REJECTED,POLL_ERROR,SUBMIT_ERROR,STOPPED }
    public enum Result { DELIVERED,DENIED,REJECTED }
    public static final class Request<P> {
        public final P body;public final String nonce;public final long expiresAt;
        public Request(P body,String nonce,long expiresAt){if(body==null)throw new IllegalArgumentException("Missing prompt");this.body=body;this.nonce=nonce;this.expiresAt=expiresAt;}
    }
    public interface Client<P> { Request<P> poll()throws Exception;Result decide(P body,boolean approved)throws Exception; }
    public interface Wearable {
        // The production WearableChannel consumes the ticket before invoking clicked.
        void prompt(PromptGate gate,PromptGate.Ticket ticket,BooleanSupplier active,Consumer<Boolean> clicked);
    }
    private final LongSupplier elapsed;private final long deadline;
    private final BooleanSupplier contextCurrent,link;
    private final Client<P> client;private final Wearable wearable;private final Executor decisions;
    private final PromptGate gate;private final VmPollRecovery recovery=new VmPollRecovery();
    private boolean stopped;private long lastNow;
    private volatile State state=State.WAIT_LINK;
    private volatile PromptGate.Ticket pending;
    private int polls,prompts,delivered,denied;
    public VmWearSession(LongSupplier wall,LongSupplier elapsed,long duration,BooleanSupplier contextCurrent,BooleanSupplier link,Client<P> client,Wearable wearable,Executor decisions){
        long now=elapsed.getAsLong();if(now<0 || duration<=0 || duration>MAX_DURATION || now>Long.MAX_VALUE-duration)throw new IllegalArgumentException("VM background duration");
        this.elapsed=elapsed;deadline=now+duration;lastNow=now;this.contextCurrent=contextCurrent;this.link=link;this.client=client;this.wearable=wearable;this.decisions=decisions;gate=new PromptGate(wall,elapsed);
    }
    public boolean active(){
        synchronized(this){if(stopped)return false;}
        boolean allowed=false;try{allowed=contextCurrent.getAsBoolean();}catch(RuntimeException ignored){}
        synchronized(this){if(stopped)return false;long now=elapsed.getAsLong();allowed=allowed && now>=lastNow && now<deadline;if(allowed){lastNow=now;return true;}}
        stop();return false;
    }
    public void stop(){synchronized(this){if(stopped)return;stopped=true;pending=null;state=State.STOPPED;}gate.stop();}
    public void invalidatePending(){PromptGate.Ticket previous;synchronized(this){previous=pending;pending=null;if(!stopped)state=State.WAIT_LINK;}if(previous!=null)gate.cancel(previous);}
    private synchronized void state(State value){if(!stopped)state=value;}
    public State state(){return state;}
    public synchronized int polls(){return polls;}public synchronized int prompts(){return prompts;}
    public synchronized int delivered(){return delivered;}public synchronized int denied(){return denied;}
    public void poll(){
        if(!active())return;
        try{
            boolean available=link.getAsBoolean();if(!recovery.ready(elapsed.getAsLong(),available)){if(!available)state(State.WAIT_LINK);return;}
            Request<P> request=client.poll();if(!active())return;recovery.succeeded();synchronized(this){polls++;}
            if(request==null){if(!gate.isActive(pending))state(State.WAIT_REQUEST);return;}
            PromptGate.Ticket ticket=gate.begin(request.nonce,request.expiresAt);if(ticket==null){if(state==State.WAIT_CLICK && !gate.isActive(pending))state(State.WAIT_REQUEST);return;}
            pending=ticket;synchronized(this){prompts++;}state(State.WAIT_CLICK);AtomicBoolean claimed=new AtomicBoolean();
            wearable.prompt(gate,ticket,this::active,approved->{
                if(!active() || !gate.canSubmit(ticket) || !claimed.compareAndSet(false,true))return;
                state(State.QUEUED);
                try{decisions.execute(()->submit(request,ticket,approved));}catch(RuntimeException error){state(State.SUBMIT_ERROR);}
            });
        }catch(Exception error){if(active()){recovery.failed(elapsed.getAsLong());state(State.POLL_ERROR);}}
    }
    private void submit(Request<P> request,PromptGate.Ticket ticket,boolean approved){
        try{
            if(!active() || !gate.canSubmit(ticket) || !link.getAsBoolean()){state(State.REJECTED);return;}
            Result result=client.decide(request.body,approved);
            if(!active())return;
            if(approved && result==Result.DELIVERED){synchronized(this){delivered++;}state(State.DELIVERED);}
            else if(!approved && result==Result.DENIED){synchronized(this){denied++;}state(State.DENIED);}
            else state(State.REJECTED);
        }catch(Exception error){state(State.SUBMIT_ERROR);} // A consumed decision is never retried.
    }
}
