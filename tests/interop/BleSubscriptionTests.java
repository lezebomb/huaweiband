package dev.bandkey.bridge;
import java.nio.file.*;
public final class BleSubscriptionTests {
    public static void main(String[] args)throws Exception{
        int count=0;
        for(int mode=0;mode<=2;mode++){
            byte[] value=BleSubscription.encode(mode);
            if(value.length!=2 || value[0]!=mode || value[1]!=0 || BleSubscription.parse(value)!=mode)throw new AssertionError("CCCD wire value");count++;
        }
        for(byte[] invalid:new byte[][]{null,{}, {1}, {2,0,0}, {3,0}, {2,1}, {(byte)255,0}}){
            if(BleSubscription.parse(invalid)!=-1)throw new AssertionError("Invalid CCCD accepted");count++;
        }
        for(int invalid:new int[]{-1,3}){boolean refused=false;try{BleSubscription.encode(invalid);}catch(IllegalArgumentException expected){refused=true;}if(!refused)throw new AssertionError("Invalid mode encoded");count++;}
        Files.writeString(Path.of(args[0]),"{\"passed\":"+count+",\"failed\":0,\"physicalDeviceTested\":false}");
        System.out.println("BLE subscription wire and compatibility checks: "+count+" passed.");
    }
}
