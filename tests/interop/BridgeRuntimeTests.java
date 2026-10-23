package dev.bandkey.bridge;

import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;

/** Exercises production lifecycle code without Huawei service access or simulated SDK success. */
public final class BridgeRuntimeTests {
    interface Check {void run()throws Exception;}
    static final List<String> results=new ArrayList<>();
    static int failed;
    static final class Fixture {
        long wall=1000,elapsed=5000;
        final PromptGate gate=new PromptGate(()->wall,()->elapsed);
        PromptGate.Ticket prompt(){return gate.begin("nonce-1",wall+30);}
    }
    static void check(boolean condition){if(!condition)throw new AssertionError("Unexpected lifecycle outcome");}
    static void test(String name,Check body){
        try{body.run();results.add("{\"name\":\""+name+"\",\"passed\":true}");System.out.println("PASS "+name);}
        catch(Throwable error){failed++;results.add("{\"name\":\""+name+"\",\"passed\":false,\"error\":\""+error.getClass().getSimpleName()+"\"}");System.err.println("FAIL "+name+": "+error);}
    }
    public static void main(String[] args)throws Exception {
        test("valid request can dispatch but delivery alone cannot submit",()->{
            Fixture f=new Fixture();var t=f.prompt();AtomicInteger sends=new AtomicInteger();
            check(f.gate.runIfActive(t,sends::incrementAndGet));check(sends.get()==1);check(!f.gate.canSubmit(t));
        });
        test("only one of concurrent button callbacks wins",()->{
            Fixture f=new Fixture();var t=f.prompt();AtomicInteger approvals=new AtomicInteger();
            ExecutorService pool=Executors.newFixedThreadPool(8);CountDownLatch start=new CountDownLatch(1);
            try{
                List<Future<?>> work=new ArrayList<>();
                for(int i=0;i<32;i++)work.add(pool.submit(()->{try{start.await();if(f.gate.consume(t))approvals.incrementAndGet();}catch(InterruptedException e){Thread.currentThread().interrupt();throw new RuntimeException(e);}}));
                start.countDown();for(Future<?> task:work)task.get(5,TimeUnit.SECONDS);
                check(approvals.get()==1);check(f.gate.canSubmit(t));
            }finally{pool.shutdownNow();}
        });
        test("second click cannot change the first decision",()->{
            Fixture f=new Fixture();var t=f.prompt();check(f.gate.consume(t));check(!f.gate.consume(t));check(!f.gate.isActive(t));
        });
        test("late device discovery after stop cannot send notification",()->{
            Fixture f=new Fixture();var t=f.prompt();f.gate.stop();
            check(!f.gate.runIfActive(t,()->{throw new AssertionError("Late SDK send");}));
        });
        test("stop invalidates pending and already clicked requests",()->{
            Fixture f=new Fixture();var t=f.prompt();check(f.gate.consume(t));f.gate.stop();check(!f.gate.canSubmit(t));check(!f.gate.consume(t));
        });
        test("changing target discards old asynchronous callback",()->{
            Fixture f=new Fixture();var old=f.prompt();var current=f.gate.begin("nonce-2",1030);
            check(!f.gate.consume(old));check(!f.gate.runIfActive(old,()->{throw new AssertionError();}));check(f.gate.consume(current));
        });
        test("replacement cancels a queued old approval",()->{
            Fixture f=new Fixture();var old=f.prompt();check(f.gate.consume(old));f.gate.begin("nonce-2",1030);check(!f.gate.canSubmit(old));
        });
        test("same server request is not shown twice",()->{
            Fixture f=new Fixture();var t=f.prompt();check(f.gate.begin("nonce-1",1030)==null);check(f.gate.isActive(t));
        });
        test("failed notification is not retried on every poll",()->{
            Fixture f=new Fixture();var t=f.prompt();f.gate.cancel(t);check(f.gate.begin("nonce-1",1030)==null);check(!f.gate.consume(t));
        });
        test("expired wall-clock request cannot dispatch or click",()->{
            Fixture f=new Fixture();var t=f.prompt();f.wall=1030;check(!f.gate.isActive(t));check(!f.gate.consume(t));
        });
        test("clock rollback does not extend a wearable request",()->{
            Fixture f=new Fixture();var t=f.prompt();f.wall=900;f.elapsed=35000;check(!f.gate.consume(t));
        });
        test("clock forward jump fails closed",()->{
            Fixture f=new Fixture();var t=f.prompt();f.wall=2000;check(!f.gate.runIfActive(t,()->{throw new AssertionError();}));
        });
        test("queued approval expires on monotonic clock",()->{
            Fixture f=new Fixture();var t=f.prompt();check(f.gate.consume(t));f.wall=900;f.elapsed=35000;check(!f.gate.canSubmit(t));
        });
        test("already expired and excessive lifetimes are rejected",()->{
            Fixture f=new Fixture();check(f.gate.begin("expired",1000)==null);check(f.gate.begin("overlong",1031)==null);check(f.gate.begin("",1030)==null);
        });
        test("new challenge works after previous expiry",()->{
            Fixture f=new Fixture();var old=f.prompt();f.wall=1031;f.elapsed=36000;var current=f.gate.begin("fresh",1061);
            check(!f.gate.consume(old));check(f.gate.consume(current));
        });
        test("cancelled old query cannot cancel the replacement",()->{
            Fixture f=new Fixture();var old=f.prompt();var current=f.gate.begin("fresh",1030);f.gate.cancel(old);check(f.gate.consume(current));
        });
        test("stopped service never reopens an old gate",()->{
            Fixture f=new Fixture();f.gate.stop();check(f.gate.begin("fresh",1030)==null);
        });
        test("network failure retry grows to a bounded sixty seconds",()->{
            RetrySchedule r=new RetrySchedule();long now=0;check(r.ready(now));
            for(long delay:new long[]{4000,8000,16000,32000,60000,60000,60000}){
                r.failed(now);check(!r.ready(now+delay-1));check(r.ready(now+delay));now+=delay;
            }
        });
        test("successful reconnect resets retry sequence",()->{
            RetrySchedule r=new RetrySchedule();r.failed(0);r.failed(4000);r.succeeded();check(r.ready(4000));
            r.failed(4000);check(!r.ready(7999));check(r.ready(8000));
        });
        test("VM poll waits for GATT and verifies HTTPS before reporting recovery",()->{
            VmPollRecovery r=new VmPollRecovery();check(!r.ready(0,false));check(r.ready(1,true));
            r.failed(1);check(!r.ready(4000,true));check(r.ready(4001,true));check(r.succeeded());check(!r.succeeded());
        });
        test("VM link reconnection resets stale backoff but not TLS verification",()->{
            VmPollRecovery r=new VmPollRecovery();r.failed(0);r.failed(4000);check(!r.ready(5000,true));
            check(!r.ready(5001,false));check(!r.ready(5002,false));check(r.ready(5003,true));
            r.failed(5003);check(!r.ready(9002,true));check(r.ready(9003,true));check(r.succeeded());
        });
        test("VM poll recovery never revives a consumed or expired authorization",()->{
            Fixture f=new Fixture();var t=f.prompt();check(f.gate.consume(t));VmPollRecovery r=new VmPollRecovery();
            r.failed(0);check(!r.ready(1,false));f.elapsed+=30000;f.wall+=30;
            check(r.ready(2,true));check(r.succeeded());check(!f.gate.canSubmit(t));check(!f.gate.consume(t));
        });
        String report="{\"generatedAt\":\""+Instant.now()+"\",\"total\":"+results.size()+",\"passed\":"+(results.size()-failed)+",\"failed\":"+failed+",\"tests\":["+String.join(",",results)+"]}";
        Files.writeString(Path.of(args[0]),report,StandardCharsets.UTF_8);
        System.out.println((results.size()-failed)+"/"+results.size()+" passed");if(failed!=0)System.exit(1);
    }
}
