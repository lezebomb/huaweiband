package dev.bandkey.bridge;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.*;

/** Production coordinator, simulated device/transport. No Windows or SDK calls. */
public final class VmWearSessionTests {
    static final class Fixture {
        long wall=1000,time=100;boolean current=true,link=true;int pollCalls,submitCalls,promptCalls;
        String next="request";boolean throwSubmit,throwPoll,rejectQueue;long pollAdvance;VmWearSession.Result result=VmWearSession.Result.DELIVERED;
        final List<Runnable> queued=new ArrayList<>();PromptGate gate;PromptGate.Ticket ticket;Consumer<Boolean> click;
        final VmWearSession<String> session=new VmWearSession<>(()->wall,()->time,60000,()->current,()->link,new VmWearSession.Client<String>(){
            public VmWearSession.Request<String> poll()throws Exception{pollCalls++;time+=pollAdvance;if(throwPoll)throw new java.io.IOException();return next==null?null:new VmWearSession.Request<>(next,next,wall+30);}
            public VmWearSession.Result decide(String body,boolean approved)throws Exception{submitCalls++;if(throwSubmit)throw new java.io.IOException();return result;}
        },(g,t,active,clicked)->{promptCalls++;gate=g;ticket=t;click=clicked;},task->{if(rejectQueue)throw new RejectedExecutionException();queued.add(task);});
        void click(boolean yes){if(gate.consume(ticket))click.accept(yes);}
        void run(){for(Runnable task:new ArrayList<>(queued))task.run();queued.clear();}
    }
    static int passed;
    static void check(boolean v){if(!v)throw new AssertionError();}
    static void test(String name,Runnable body){body.run();passed++;System.out.println("PASS VM background "+name);}
    public static void main(String[] args)throws Exception{
        test("notification delivery alone cannot sign",()->{Fixture f=new Fixture();f.session.poll();check(f.promptCalls==1 && f.submitCalls==0 && f.queued.isEmpty());});
        test("repeated signed prompt sends one wearable notification",()->{Fixture f=new Fixture();f.session.poll();f.session.poll();check(f.promptCalls==1);});
        test("one consumed click reaches the decision adapter",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.run();check(f.submitCalls==1 && f.session.delivered()==1);});
        test("duplicate callback cannot queue a second decision",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.click.accept(true);check(f.queued.size()==1);f.run();check(f.submitCalls==1);});
        test("signed denial cannot become approval",()->{Fixture f=new Fixture();f.result=VmWearSession.Result.DENIED;f.session.poll();f.click(false);f.run();check(f.submitCalls==1 && f.session.denied()==1 && f.session.delivered()==0);});
        test("stop discards queued decision",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.session.stop();f.run();check(f.submitCalls==0 && !f.session.active());});
        test("expiry discards queued decision",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.wall+=30;f.time+=30000;f.run();check(f.submitCalls==0);});
        test("context replacement discards queued decision",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.current=false;f.run();check(f.submitCalls==0 && !f.session.active());});
        test("link loss discards click without signing or replay",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.link=false;f.run();f.link=true;f.session.poll();f.run();check(f.submitCalls==0 && f.promptCalls==1);});
        test("replaced challenge invalidates earlier queued decision",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.next="new";f.session.poll();f.run();check(f.submitCalls==0 && f.promptCalls==2);});
        test("failed submission is never retried by polls",()->{Fixture f=new Fixture();f.throwSubmit=true;f.session.poll();f.click(true);f.run();f.session.poll();f.run();check(f.submitCalls==1);});
        test("unconsumed callback cannot sign",()->{Fixture f=new Fixture();f.session.poll();f.click.accept(true);f.run();check(f.submitCalls==0);});
        test("GATT absence avoids HTTPS and reconnect resumes polling",()->{Fixture f=new Fixture();f.link=false;f.session.poll();check(f.pollCalls==0);f.link=true;f.session.poll();check(f.pollCalls==1);});
        test("lease deadline rejects before delayed timer",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.time=60100;f.run();check(f.submitCalls==0 && !f.session.active());});
        test("monotonic rollback cancels run",()->{Fixture f=new Fixture();f.time=99;check(!f.session.active());});
        test("network failure backoff does not spin",()->{Fixture f=new Fixture();f.throwPoll=true;f.session.poll();f.session.poll();check(f.pollCalls==1);f.time+=4000;f.session.poll();check(f.pollCalls==2);});
        test("late poll result cannot prompt after lease expiry",()->{Fixture f=new Fixture();f.pollAdvance=60000;f.session.poll();check(f.promptCalls==0 && !f.session.active());});
        test("SDK cancellation clears waiting state without resending",()->{Fixture f=new Fixture();f.session.poll();f.gate.cancel(f.ticket);f.session.poll();check(f.promptCalls==1 && f.session.state()==VmWearSession.State.WAIT_REQUEST);});
        test("executor refusal cannot retry a consumed click",()->{Fixture f=new Fixture();f.rejectQueue=true;f.session.poll();f.click(true);f.rejectQueue=false;f.session.poll();f.run();check(f.submitCalls==0 && f.session.state()==VmWearSession.State.SUBMIT_ERROR);});
        test("unexpected denial response never records an approval",()->{Fixture f=new Fixture();f.result=VmWearSession.Result.DENIED;f.session.poll();f.click(true);f.run();check(f.session.delivered()==0 && f.session.state()==VmWearSession.State.REJECTED);});
        test("departing band invalidates an already queued click",()->{Fixture f=new Fixture();f.session.poll();f.click(true);f.session.invalidatePending();f.run();check(f.submitCalls==0 && f.session.active());});
        test("new native request can prompt after arrival cancels old notification",()->{Fixture f=new Fixture();f.session.poll();Consumer<Boolean> old=f.click;f.session.invalidatePending();f.next="fresh-return";f.session.poll();old.accept(true);f.run();check(f.promptCalls==2 && f.submitCalls==0);f.click(true);f.run();check(f.submitCalls==1);});
        test("brief link outage preserves unconsumed current request",()->{Fixture f=new Fixture();f.session.poll();f.link=false;f.session.poll();f.link=true;f.session.poll();f.click(true);f.run();check(f.promptCalls==1 && f.submitCalls==1);});
        Files.writeString(Path.of(args[0]),"{\"passed\":"+passed+",\"failed\":0,\"simulatedDevice\":true,\"windowsAuthenticationSubmitted\":false}");
        System.out.println("VM background coordinator: "+passed+" checks passed.");
    }
}
