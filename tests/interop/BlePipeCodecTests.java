package dev.bandkey.bridge;

import java.nio.file.*;
import java.io.*;
import java.util.*;

public final class BlePipeCodecTests {
    static int passed;static void check(boolean value){if(!value)throw new AssertionError("BLE codec");passed++;}
    static void refuses(Runnable action){try{action.run();}catch(IllegalArgumentException error){passed++;return;}throw new AssertionError("BLE accepted invalid input");}
    public static void main(String[] args)throws Exception{
        check(HexFormat.of().formatHex(new BlePipeFrame(3,0x0102030405060708L,2,new byte[]{(byte)0xaa,(byte)0xbb}).encode()).equals("03080706050403020102000000aabb"));
        for(int kind=1;kind<=5;kind++){var f=new BlePipeFrame(kind,-1L,65535,kind==3?new byte[160]:new byte[0]);check(Arrays.equals(f.encode(),BlePipeFrame.decode(f.encode()).encode()));}
        for(int size:new int[]{0,12,174})refuses(()->BlePipeFrame.decode(new byte[size]));
        refuses(()->new BlePipeFrame(6,1,0,new byte[0]));refuses(()->new BlePipeFrame(1,0,0,new byte[0]));refuses(()->new BlePipeFrame(1,1,-1,new byte[0]));refuses(()->new BlePipeFrame(1,1,65536,new byte[0]));refuses(()->new BlePipeFrame(3,1,0,new byte[0]));refuses(()->new BlePipeFrame(1,1,0,new byte[]{1}));
        for(var bad:new BlePipeFrame[]{new BlePipeFrame(3,1,0,new byte[]{1}),new BlePipeFrame(3,1,2,new byte[]{1}),new BlePipeFrame(3,2,1,new byte[]{1})}){var receiver=new BlePipeFrame.Receiver(1,1);receiver.accept(new BlePipeFrame(1,1,0,new byte[0]));refuses(()->receiver.accept(bad));}
        for(int kind:new int[]{4,5}){var receiver=new BlePipeFrame.Receiver(1,2);receiver.accept(new BlePipeFrame(2,1,0,new byte[0]));receiver.accept(new BlePipeFrame(kind,1,1,new byte[0]));refuses(()->receiver.accept(new BlePipeFrame(3,1,2,new byte[]{1})));}
        var budget=new BlePipeFrame.Receiver(1,1);budget.accept(new BlePipeFrame(1,1,0,new byte[0]));int seq=1;for(int i=0;i<819;i++)budget.accept(new BlePipeFrame(3,1,seq++,new byte[160]));budget.accept(new BlePipeFrame(3,1,seq++,new byte[32]));final int overflow=seq;refuses(()->budget.accept(new BlePipeFrame(3,1,overflow,new byte[]{1})));
        Random random=new Random(81973);boolean okay=true;for(int i=0;i<1000;i++){byte[] data=new byte[random.nextInt(160)+1];random.nextBytes(data);var frame=new BlePipeFrame(3,random.nextLong()|1,random.nextInt(65536),data);okay&=Arrays.equals(frame.encode(),BlePipeFrame.decode(frame.encode()).encode());}check(okay);
        if(args.length>1){
            Path dir=Path.of(args[1]);
            try(InputStream input=Files.newInputStream(dir.resolve("csharp.bin"));OutputStream output=Files.newOutputStream(dir.resolve("java-roundtrip.bin"));OutputStream generated=Files.newOutputStream(dir.resolve("java.bin"))){
                for(int i=0;i<1000;i++){int low=input.read(),high=input.read();if(low<0 || high<0)throw new IOException("Missing CSharp frame");int length=low|(high<<8);byte[] bytes=input.readNBytes(length);var frame=BlePipeFrame.decode(bytes);check(frame.kind==3 && frame.session==0xfedcba9876540000L+i && frame.sequence==i && frame.payload.length==i%160+1);byte[] encoded=frame.encode();output.write(encoded.length&255);output.write(encoded.length>>8);output.write(encoded);
                    byte[] data=new byte[i%160+1];Arrays.fill(data,(byte)i);byte[] reply=new BlePipeFrame(3,0xfedcba9876540000L+i,i,data).encode();generated.write(reply.length&255);generated.write(reply.length>>8);generated.write(reply);
                }if(input.read()!=-1)throw new IOException("Trailing CSharp frame");
            }
        }
        Files.writeString(Path.of(args[0]),"{\"passed\":"+passed+",\"failed\":0,\"simulated\":true,\"physicalBleTested\":false}");
        System.out.println("BLE production Java frame codec: "+passed+" checks passed; includes 1000 randomized roundtrips.");
    }
}
