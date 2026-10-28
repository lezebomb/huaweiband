package dev.bandkey.bridge;

import java.io.*;
import java.net.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

/** One opaque TLS stream. Local TCP closure must not discard the GATT framing boundary. */
final class BleSocketRelay {
    interface Sender {void send(int kind,byte[] payload)throws Exception;}
    static BlePipeFrame awaitReady(BlockingQueue<BlePipeFrame> incoming,BooleanSupplier valid,LongSupplier elapsed,long deadline)throws Exception{
        while(valid.getAsBoolean() && elapsed.getAsLong()<deadline){
            BlePipeFrame frame=incoming.poll(100,TimeUnit.MILLISECONDS);
            if(!valid.getAsBoolean())throw new IOException("BLE ready generation ended");
            if(frame!=null)return frame;
        }
        throw new IOException("BLE ready timeout or generation ended");
    }
    static boolean run(Socket client,BlePipeFrame.Receiver receiver,BlockingQueue<BlePipeFrame> incoming,
        Sender sender,IntSupplier payloadSize,BooleanSupplier available,LongSupplier elapsed,long deadline,ExecutorService workers)throws Exception{
        AtomicReference<Exception> firstFailure=new AtomicReference<>();AtomicBoolean localAbort=new AtomicBoolean();
        Future<?> download=workers.submit(()->{
            try{
                while(available.getAsBoolean() && !client.isClosed() && elapsed.getAsLong()<deadline){
                    BlePipeFrame frame=incoming.poll(1,TimeUnit.SECONDS);if(frame==null)continue;
                    if(!available.getAsBoolean())throw new IOException("BLE transport lease ended");receiver.accept(frame);
                    if(frame.kind==BlePipeFrame.ABORT)throw new IOException("BLE abort");
                    if(frame.kind==BlePipeFrame.EOF){
                        try{client.shutdownOutput();}catch(IOException error){if(!available.getAsBoolean() || client.isClosed())throw error;localAbort.set(true);}return;
                    }
                    if(!localAbort.get())try{client.getOutputStream().write(frame.payload);}
                    catch(IOException error){
                        if(!available.getAsBoolean() || client.isClosed())throw error;
                        // The local HTTPS caller has closed. Validate and drain this
                        // session through EOF; truncated HTTP/TLS still fails there.
                        localAbort.set(true);
                    }
                }
                throw new IOException("BLE stream lifetime");
            }catch(Exception error){firstFailure.compareAndSet(null,error);try{client.close();}catch(IOException ignored){}throw new CompletionException(error);}
        });
        int bytes=0;
        try{
            while(available.getAsBoolean() && elapsed.getAsLong()<deadline){
                int size=payloadSize.getAsInt();if(size<1 || size>BlePipeFrame.MAX_PAYLOAD)throw new IOException("BLE payload limit");
                byte[] buffer=new byte[size];int read;
                try{read=client.getInputStream().read(buffer);}catch(SocketTimeoutException ignored){continue;}
                catch(SocketException error){if(!available.getAsBoolean() || client.isClosed())throw error;localAbort.set(true);read=-1;}
                if(read<0){sender.send(BlePipeFrame.EOF,new byte[0]);download.get(5,TimeUnit.SECONDS);return localAbort.get();}
                bytes+=read;if(bytes>BlePipeFrame.MAX_BYTES)throw new IOException("BLE stream limit");sender.send(BlePipeFrame.DATA,Arrays.copyOf(buffer,read));
            }
            throw new IOException("BLE stream lifetime");
        }catch(Exception error){Exception first=firstFailure.get();throw first!=null?first:error;}
        finally{download.cancel(true);}
    }
}
