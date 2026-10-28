package dev.bandkey.bridge;

import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

/** Real loopback sockets, with a framed peer standing in for the GATT transport. */
public final class BleSocketRelayTests {
    private static final long SESSION=39;
    private static final class Link implements AutoCloseable {
        final ServerSocket listener=new ServerSocket(0,1,InetAddress.getLoopbackAddress());
        final Socket caller=new Socket(InetAddress.getLoopbackAddress(),listener.getLocalPort());
        final Socket accepted=listener.accept();
        final BlockingQueue<BlePipeFrame> incoming=new LinkedBlockingQueue<>();
        final BlockingQueue<Integer> sent=new LinkedBlockingQueue<>();
        final ExecutorService workers=Executors.newCachedThreadPool();
        final AtomicBoolean available=new AtomicBoolean(true);
        final Future<Boolean> result;
        Link()throws Exception {
            accepted.setSoTimeout(100);caller.setSoTimeout(5000);
            BlePipeFrame.Receiver receiver=new BlePipeFrame.Receiver(SESSION,BlePipeFrame.READY);
            receiver.accept(new BlePipeFrame(BlePipeFrame.READY,SESSION,0,new byte[0]));
            result=workers.submit(()->BleSocketRelay.run(accepted,receiver,incoming,
                (kind,payload)->sent.add(kind),()->160,available::get,
                ()->System.nanoTime()/1000000,System.nanoTime()/1000000+10000,workers));
        }
        void awaitSent(int expected)throws Exception {
            Integer kind=sent.poll(5,TimeUnit.SECONDS);
            if(kind==null || kind!=expected)throw new AssertionError("Expected outgoing kind "+expected+", got "+kind);
        }
        void resetCaller()throws Exception {
            caller.getOutputStream().write(new byte[]{1});caller.getOutputStream().flush();
            awaitSent(BlePipeFrame.DATA);caller.setSoLinger(true,0);caller.close();awaitSent(BlePipeFrame.EOF);
        }
        void incoming(int kind,long session,int sequence,byte[] payload){incoming.add(new BlePipeFrame(kind,session,sequence,payload));}
        public void close()throws Exception {
            available.set(false);caller.close();accepted.close();listener.close();result.cancel(true);
            workers.shutdownNow();if(!workers.awaitTermination(5,TimeUnit.SECONDS))throw new AssertionError("Socket workers leaked");
        }
    }
    private static void resetDrainsThroughEof()throws Exception {
        try(Link link=new Link()){
            link.resetCaller();
            link.incoming(BlePipeFrame.DATA,SESSION,1,new byte[]{2,3,4});
            if(link.result.isDone())throw new AssertionError("Returned before matching remote EOF");
            link.incoming(BlePipeFrame.EOF,SESSION,2,new byte[0]);
            if(!link.result.get(5,TimeUnit.SECONDS))throw new AssertionError("Local reset was not recorded");
            if(!link.incoming.isEmpty())throw new AssertionError("Late stream frames were not drained");
        }
    }
    private static void halfClosePreservesResponse()throws Exception {
        try(Link link=new Link()){
            link.caller.shutdownOutput();link.awaitSent(BlePipeFrame.EOF);
            byte[] response={5,6,7,8};link.incoming(BlePipeFrame.DATA,SESSION,1,response);
            link.incoming(BlePipeFrame.EOF,SESSION,2,new byte[0]);
            if(!Arrays.equals(response,link.caller.getInputStream().readAllBytes()))throw new AssertionError("Response truncated");
            if(link.result.get(5,TimeUnit.SECONDS))throw new AssertionError("Normal half-close counted as abort");
        }
    }
    private static void invalidDrainStillFails()throws Exception {
        try(Link link=new Link()){
            link.resetCaller();link.incoming(BlePipeFrame.DATA,SESSION+1,1,new byte[]{2});
            try{link.result.get(5,TimeUnit.SECONDS);throw new AssertionError("Wrong-session frame accepted during drain");}
            catch(ExecutionException expected){if(!(expected.getCause() instanceof IllegalArgumentException))throw expected;}
        }
    }
    private static void cancelledLeaseStillFails()throws Exception {
        try(Link link=new Link()){
            link.available.set(false);link.accepted.close();
            try{link.result.get(5,TimeUnit.SECONDS);throw new AssertionError("Cancelled lease accepted");}
            catch(ExecutionException expected){if(!(expected.getCause() instanceof IOException))throw expected;}
            if(!link.sent.isEmpty())throw new AssertionError("Cancellation emitted a successful EOF");
        }
    }
    public static void main(String[] args)throws Exception {
        resetDrainsThroughEof();halfClosePreservesResponse();invalidDrainStillFails();cancelledLeaseStillFails();
        Files.writeString(Path.of(args[0]),"{\"passed\":4,\"failed\":0,\"realLoopbackSockets\":true,\"physicalBleTested\":false}");
        System.out.println("BLE local socket closure checks: 4 passed.");
    }
}
