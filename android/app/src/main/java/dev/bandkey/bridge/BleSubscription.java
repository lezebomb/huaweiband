package dev.bandkey.bridge;

/** Supported CCCD modes. ATT acknowledgement never substitutes for TLS identity. */
public final class BleSubscription {
    public static final int DISABLED=0,NOTIFY=1,INDICATE=2;
    public static int parse(byte[] value){
        if(value==null || value.length!=2 || value[1]!=0 || value[0]<0 || value[0]>2)return -1;
        return value[0];
    }
    public static byte[] encode(int mode){
        if(mode<0 || mode>2)throw new IllegalArgumentException("BLE subscription mode");
        return new byte[]{(byte)mode,0};
    }
}
