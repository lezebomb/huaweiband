package dev.bandkey.bridge;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;

/** Production lease with fake radio observations and actual Socket cleanup; no Android hardware. */
public final class BleBackgroundLeaseTests {
    interface Check {void run()throws Exception;}
    static final List<String> results=new ArrayList<>();static int failed;
    static void check(boolean value){if(!value)throw new AssertionError("Unexpected BLE lease outcome");}
    static void test(String name,Check body){
        try{body.run();results.add("{\"name\":\""+name+"\",\"passed\":true}");System.out.println("PASS "+name);}
        catch(Throwable error){failed++;results.add("{\"name\":\""+name+"\",\"passed\":false,\"error\":\""+error.getClass().getSimpleName()+"\"}");System.err.println("FAIL "+name+": "+error);}
    }
    static final class Radio implements BleBackgroundLease.Transport {
        int starts,closes;boolean failStart,failClose,failStats;
        BleBackgroundLease.Stats stats=new BleBackgroundLease.Stats(true,false,false,0,0,0);
        public void start()throws Exception{starts++;if(failStart)throw new IOException("synthetic startup");}
        public BleBackgroundLease.Stats stats(){if(failStats)throw new SecurityException("synthetic permission");return stats;}
        public void close(){closes++;if(failClose)throw new IllegalStateException("synthetic cleanup");}
        void state(boolean connected,boolean stream,long sent,long received,int streams){stats=new BleBackgroundLease.Stats(true,connected,stream,sent,received,streams);}
    }
    static final class Fixture {
        final BleBackgroundLease lease=new BleBackgroundLease();final Radio radio=new Radio();
        void start(){check(lease.start(1000,30000,()->radio));}
        void tick(long now){lease.tick(now,true);}
        BleBackgroundLease.Snapshot s(){return lease.snapshot();}
    }
    public static void main(String[] args)throws Exception{
        test("new process has no active lease or persisted restart",()->{Fixture f=new Fixture();check(!f.lease.allows(0));f.tick(1000);check(f.s().stage==BleBackgroundLease.Stage.IDLE && f.radio.starts==0);});
        test("explicit run advertises then observes connected stream",()->{Fixture f=new Fixture();f.start();f.tick(1100);check(f.s().stage==BleBackgroundLease.Stage.ADVERTISING);f.radio.state(true,true,25,50,1);f.tick(1200);check(f.s().stage==BleBackgroundLease.Stage.STREAMING && f.s().sent==25 && f.s().received==50);});
        test("duplicate start cannot replace resource or renew deadline",()->{Fixture f=new Fixture();f.start();check(!f.lease.start(20000,30000,()->{throw new AssertionError("Duplicate allocation");}));f.tick(31000);check(f.s().reason==BleBackgroundLease.Reason.DEADLINE && f.radio.starts==1 && f.radio.closes==1);});
        test("exact deadline blocks IO even before delayed Android timer runs",()->{Fixture f=new Fixture();f.start();check(f.lease.allows(30999));check(!f.lease.allows(31000));f.tick(60000);check(f.radio.closes==1 && f.s().reason==BleBackgroundLease.Reason.DEADLINE);});
        test("long sleep cannot extend transport lifetime",()->{Fixture f=new Fixture();f.start();check(!f.lease.allows(3600000));f.tick(3600000);check(f.s().remaining==0 && f.radio.closes==1);});
        test("monotonic clock rollback stops old run",()->{Fixture f=new Fixture();f.start();f.tick(2000);check(!f.lease.allows(1999));f.tick(1999);check(f.s().reason==BleBackgroundLease.Reason.CLOCK_ROLLBACK && f.radio.closes==1);});
        test("bluetooth unavailable stops instead of silently restarting",()->{Fixture f=new Fixture();f.start();f.lease.tick(2000,false);f.tick(3000);check(f.s().reason==BleBackgroundLease.Reason.BLUETOOTH_UNAVAILABLE && f.radio.closes==1 && !f.lease.allows(3000));});
        test("disconnect remains recoverable within original lease",()->{Fixture f=new Fixture();f.start();f.radio.state(true,true,100,200,1);f.tick(2000);f.radio.state(false,false,100,200,1);f.tick(3000);check(f.s().disconnects==1 && f.radio.closes==0);f.tick(4000);check(f.s().disconnects==1);f.radio.state(true,false,120,240,2);f.tick(5000);check(f.s().stage==BleBackgroundLease.Stage.CONNECTED && f.lease.allows(5000));});
        test("startup exception releases partially created resource",()->{Fixture f=new Fixture();f.radio.failStart=true;check(!f.lease.start(1000,30000,()->f.radio));check(f.s().reason==BleBackgroundLease.Reason.START_FAILED && f.radio.closes==1);});
        test("factory exception is a stopped run without second attempt",()->{Fixture f=new Fixture();check(!f.lease.start(1000,30000,()->{throw new IOException("synthetic allocation");}));check(f.s().reason==BleBackgroundLease.Reason.START_FAILED);check(!f.lease.start(2000,30000,()->f.radio));check(f.radio.starts==0);});
        test("advertising timeout bounds failed asynchronous startup",()->{Fixture f=new Fixture();f.radio.stats=new BleBackgroundLease.Stats(false,false,false,0,0,0);f.start();f.tick(10999);check(f.radio.closes==0);f.tick(11000);check(f.s().reason==BleBackgroundLease.Reason.ADVERTISING_TIMEOUT && f.radio.closes==1);});
        test("permission or radio observation failure stops resource",()->{Fixture f=new Fixture();f.start();f.radio.failStats=true;f.tick(2000);check(f.s().reason==BleBackgroundLease.Reason.OBSERVATION_FAILED && f.radio.closes==1);});
        test("regressing counters invalidate evidence and stop run",()->{Fixture f=new Fixture();f.start();f.radio.state(true,false,100,200,2);f.tick(2000);f.radio.state(true,false,99,200,2);f.tick(3000);check(f.s().reason==BleBackgroundLease.Reason.OBSERVATION_FAILED && f.s().sent==100);});
        test("stop is final and idempotent across lifecycle paths",()->{Fixture f=new Fixture();f.start();f.lease.stop(BleBackgroundLease.Reason.USER);f.lease.stop(BleBackgroundLease.Reason.DESTROYED);f.tick(40000);check(f.radio.closes==1 && f.s().reason==BleBackgroundLease.Reason.USER && !f.lease.allows(1001));});
        test("task removal and destruction close resource",()->{for(var reason:new BleBackgroundLease.Reason[]{BleBackgroundLease.Reason.TASK_REMOVED,BleBackgroundLease.Reason.DESTROYED}){Fixture f=new Fixture();f.start();f.lease.stop(reason);check(f.radio.closes==1 && f.s().reason==reason);}});
        test("cleanup failure is reported without reactivating lease",()->{Fixture f=new Fixture();f.start();f.radio.failClose=true;f.lease.stop(BleBackgroundLease.Reason.USER);check(f.s().cleanupFailed && !f.lease.allows(1001));f.lease.stop(BleBackgroundLease.Reason.USER);check(f.radio.closes==1);});
        test("event record is bounded and immutable",()->{Fixture f=new Fixture();f.start();for(int i=0;i<150;i++){f.radio.state(i%2==0,false,0,0,0);f.tick(1001+i);}var snapshot=f.s();check(snapshot.truncated && snapshot.events.size()==64 && snapshot.disconnects==75);boolean immutable=false;try{snapshot.events.clear();}catch(UnsupportedOperationException expected){immutable=true;}check(immutable);});
        test("invalid lifetime cannot allocate radio",()->{for(long duration:new long[]{0,-1,BleBackgroundLease.MAX_DURATION+1,Long.MAX_VALUE}){Fixture f=new Fixture();boolean refused=false;try{f.lease.start(1000,duration,()->f.radio);}catch(IllegalArgumentException expected){refused=true;}check(refused && f.radio.starts==0 && f.s().stage==BleBackgroundLease.Stage.IDLE);}});
        test("maximum lease is allowed but never renewed",()->{Fixture f=new Fixture();check(f.lease.start(1000,BleBackgroundLease.MAX_DURATION,()->f.radio));check(f.lease.allows(1000+BleBackgroundLease.MAX_DURATION-1));f.tick(1000+BleBackgroundLease.MAX_DURATION);check(f.radio.closes==1);});
        test("concurrent stop paths close the resource once",()->{Fixture f=new Fixture();f.start();ExecutorService workers=Executors.newFixedThreadPool(4);try{List<Future<?>> tasks=new ArrayList<>();for(int i=0;i<32;i++)tasks.add(workers.submit(()->f.lease.stop(BleBackgroundLease.Reason.USER)));for(var task:tasks)task.get(5,TimeUnit.SECONDS);}finally{workers.shutdownNow();}check(f.radio.closes==1);});
        test("actual socket startup failure releases occupied listening port",()->{
            BleBackgroundLease lease=new BleBackgroundLease();ServerSocket listener=new ServerSocket(0,1,InetAddress.getLoopbackAddress());int port=listener.getLocalPort();
            check(!lease.start(1000,30000,()->new BleBackgroundLease.Transport(){public void start()throws Exception{throw new IOException("synthetic post-bind failure");}public BleBackgroundLease.Stats stats(){throw new AssertionError();}public void close(){try{listener.close();}catch(IOException e){throw new UncheckedIOException(e);}}}));
            check(listener.isClosed());try(ServerSocket rebound=new ServerSocket(port,1,InetAddress.getLoopbackAddress())){check(rebound.getLocalPort()==port);}
        });
        test("stopping lease cancels actual blocked socket read",()->{
            BleBackgroundLease lease=new BleBackgroundLease();ExecutorService worker=Executors.newSingleThreadExecutor();
            try(ServerSocket listener=new ServerSocket(0,1,InetAddress.getLoopbackAddress());Socket client=new Socket(InetAddress.getLoopbackAddress(),listener.getLocalPort());Socket server=listener.accept()){
                check(lease.start(1000,30000,()->new BleBackgroundLease.Transport(){public void start(){}public BleBackgroundLease.Stats stats(){return new BleBackgroundLease.Stats(true,true,true,0,0,1);}public void close(){try{server.close();}catch(IOException error){throw new UncheckedIOException(error);}}}));
                CountDownLatch reading=new CountDownLatch(1);Future<Boolean> ended=worker.submit(()->{reading.countDown();try{server.getInputStream().read();return false;}catch(SocketException expected){return true;}});
                check(reading.await(3,TimeUnit.SECONDS));lease.stop(BleBackgroundLease.Reason.USER);check(ended.get(3,TimeUnit.SECONDS));
            }finally{worker.shutdownNow();}
        });
        String report="{\"generatedAt\":\""+Instant.now()+"\",\"total\":"+results.size()+",\"passed\":"+(results.size()-failed)+",\"failed\":"+failed+",\"physicalBleTested\":false,\"androidServiceTested\":false,\"windowsAuthenticationSubmitted\":false,\"tests\":["+String.join(",",results)+"]}";
        Files.writeString(Path.of(args[0]),report,StandardCharsets.UTF_8);System.out.println((results.size()-failed)+"/"+results.size()+" passed");if(failed!=0)System.exit(1);
    }
}
