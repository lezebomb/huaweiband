package dev.bandkey.bridge;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Owns one explicit, non-renewable transport run. No account, prompt or signing state. */
final class BleBackgroundLease {
    static final long MAX_DURATION=30*60*1000L, START_TIMEOUT=10000;
    enum Stage { IDLE, STARTING, ADVERTISING, CONNECTED, STREAMING, STOPPED }
    enum Reason { NONE, USER, DEADLINE, CLOCK_ROLLBACK, BLUETOOTH_UNAVAILABLE, START_FAILED, ADVERTISING_TIMEOUT, TASK_REMOVED, DESTROYED, OBSERVATION_FAILED }
    interface Transport extends AutoCloseable {
        void start() throws Exception;
        Stats stats();
        @Override void close();
    }
    interface Factory { Transport create() throws Exception; }
    static final class Stats {
        final boolean advertising,connected,streaming;
        final long sent,received;
        final int streams;
        Stats(boolean advertising,boolean connected,boolean streaming,long sent,long received,int streams){
            if(sent<0 || received<0 || streams<0 || (streaming && !connected))throw new IllegalArgumentException("BLE statistics");
            this.advertising=advertising;this.connected=connected;this.streaming=streaming;this.sent=sent;this.received=received;this.streams=streams;
        }
    }
    static final class Event {
        final long elapsed;final Stage stage;final Reason reason;
        Event(long elapsed,Stage stage,Reason reason){this.elapsed=elapsed;this.stage=stage;this.reason=reason;}
    }
    static final class Snapshot {
        final Stage stage;final Reason reason;final long elapsed,remaining,sent,received;
        final int streams,disconnects,restarts;final boolean truncated,cleanupFailed;final List<Event> events;
        Snapshot(Stage stage,Reason reason,long elapsed,long remaining,Stats stats,int disconnects,boolean truncated,boolean cleanupFailed,List<Event> events,int restarts){
            this.stage=stage;this.reason=reason;this.elapsed=elapsed;this.remaining=remaining;sent=stats.sent;received=stats.received;streams=stats.streams;
            this.disconnects=disconnects;this.restarts=restarts;this.truncated=truncated;this.cleanupFailed=cleanupFailed;this.events=Collections.unmodifiableList(new ArrayList<>(events));
        }
    }
    private final List<Event> events=new ArrayList<>();
    private Stage stage=Stage.IDLE;private Reason reason=Reason.NONE;
    private long began,lastNow,deadline,advertisingBegan,baseSent,baseReceived;
    private boolean started,wasConnected,truncated,cleanupFailed;
    private int disconnects,restarts,baseStreams;
    private Transport transport;
    private Factory factory;
    private Stats stats=new Stats(false,false,false,0,0,0);

    private final boolean resident;
    BleBackgroundLease(){this(false);}
    BleBackgroundLease(boolean resident){this.resident=resident;}
    synchronized boolean start(long now,long duration,Factory factory){
        if(started)return false; // Duplicate Android start commands cannot extend the lease.
        if(now<0 || duration<=0 || duration>MAX_DURATION || now>Long.MAX_VALUE-duration || factory==null)throw new IllegalArgumentException("BLE run duration");
        started=true;this.factory=factory;began=lastNow=advertisingBegan=now;deadline=resident?Long.MAX_VALUE:now+duration;change(Stage.STARTING,Reason.NONE);
        try{transport=factory.create();if(transport==null)throw new IllegalStateException("Missing transport");transport.start();return true;}
        catch(Exception error){stop(Reason.START_FAILED);return false;}
    }
    // Read-only guard also used immediately before GATT/socket operations. Deep sleep does not
    // extend elapsedRealtime, even if Android postpones our next UI/notification timer.
    synchronized boolean allows(long now){return started && stage!=Stage.STOPPED && now>=lastNow && now<deadline;}
    private void observeStats(){
        Stats raw=transport.stats();
        Stats next=new Stats(raw.advertising,raw.connected,raw.streaming,Math.addExact(baseSent,raw.sent),Math.addExact(baseReceived,raw.received),Math.addExact(baseStreams,raw.streams));
        if(next.sent<stats.sent || next.received<stats.received || next.streams<stats.streams)throw new IllegalStateException("BLE counters regressed");
        stats=next;if(wasConnected && !stats.connected)disconnects++;wasConnected=stats.connected;
    }
    // Replaces only this run's radio/listener. Its original deadline and explicit
    // stop remain authoritative; a transport restart grants no signing authority.
    synchronized boolean restart(long now){
        if(!started || stage==Stage.STOPPED)return false;
        if(now<lastNow){stop(Reason.CLOCK_ROLLBACK);return false;}
        lastNow=now;if(now>=deadline){stop(Reason.DEADLINE);return false;}
        try{observeStats();}catch(RuntimeException error){stop(Reason.OBSERVATION_FAILED);return false;}
        Transport old=transport;transport=null;
        try{old.close();}catch(RuntimeException error){cleanupFailed=true;stop(Reason.START_FAILED);return false;}
        baseSent=stats.sent;baseReceived=stats.received;baseStreams=stats.streams;
        if(wasConnected)disconnects++;wasConnected=false;stats=new Stats(false,false,false,baseSent,baseReceived,baseStreams);
        advertisingBegan=now;restarts++;change(Stage.STARTING,Reason.NONE);
        try{transport=factory.create();if(transport==null)throw new IllegalStateException("Missing transport");transport.start();return true;}
        catch(Exception error){stop(Reason.START_FAILED);return false;}
    }
    synchronized void tick(long now,boolean bluetoothAllowed){
        if(!started || stage==Stage.STOPPED)return;
        if(now<lastNow){stop(Reason.CLOCK_ROLLBACK);return;}
        lastNow=now;
        if(now>=deadline){stop(Reason.DEADLINE);return;}
        if(!bluetoothAllowed){stop(Reason.BLUETOOTH_UNAVAILABLE);return;}
        try{
            observeStats();
            if(!stats.advertising && now-advertisingBegan>=START_TIMEOUT){stop(Reason.ADVERTISING_TIMEOUT);return;}
            change(stats.streaming?Stage.STREAMING:stats.connected?Stage.CONNECTED:stats.advertising?Stage.ADVERTISING:Stage.STARTING,Reason.NONE);
        }catch(RuntimeException error){stop(Reason.OBSERVATION_FAILED);}
    }
    synchronized void stop(Reason why){
        if(why==null || why==Reason.NONE)throw new IllegalArgumentException("BLE stop reason");
        if(!started || stage==Stage.STOPPED)return;
        change(Stage.STOPPED,why);Transport owned=transport;transport=null;
        if(owned!=null)try{owned.close();}catch(RuntimeException error){cleanupFailed=true;}
    }
    private void change(Stage next,Reason why){
        if(stage==next && reason==why)return;stage=next;reason=why;
        if(events.size()<64)events.add(new Event(lastNow-began,stage,reason));else truncated=true;
    }
    synchronized Snapshot snapshot(){return new Snapshot(stage,reason,started?lastNow-began:0,stage==Stage.STOPPED || !started?0:Math.max(0,deadline-lastNow),stats,disconnects,truncated,cleanupFailed,events,restarts);}
}
