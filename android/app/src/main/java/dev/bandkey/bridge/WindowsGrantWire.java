package dev.bandkey.bridge;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.interfaces.ECPublicKey;
import java.security.spec.*;
import java.math.BigInteger;
import java.util.Arrays;

/** Pure Java native-grant codec. Android Keystore signs DER; the native Provider verifies P1363. */
public final class WindowsGrantWire {
    public static final int CHALLENGE_SIZE=436, APPROVAL_SIZE=500;
    public static final long FILETIME_UNIX_MILLISECONDS=11644473600000L;
    private static final byte[] DOMAIN="BANDKEY-WIN-V1\0\0".getBytes(StandardCharsets.US_ASCII);
    private WindowsGrantWire(){}
    public static final class Challenge {
        public final long session,issued,expires;
        public final String sid;
        public final byte[] nonce;
        private Challenge(long session,long issued,long expires,String sid,byte[] nonce){this.session=session;this.issued=issued;this.expires=expires;this.sid=sid;this.nonce=nonce;}
    }
    public static Challenge read(byte[] body){
        if(body==null || body.length!=CHALLENGE_SIZE || !Arrays.equals(Arrays.copyOf(body,16),DOMAIN))throw new IllegalArgumentException("Native grant domain/length");
        ByteBuffer buffer=ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN);
        long session=Integer.toUnsignedLong(buffer.getInt(16)),issued=buffer.getLong(20),expires=buffer.getLong(28);
        if(session==0 || issued<0 || issued>Long.MAX_VALUE-30000 || expires-issued!=30000)throw new IllegalArgumentException("Native grant context");
        int end=-1;for(int i=68;i<body.length;i+=2)if(body[i]==0 && body[i+1]==0){end=i;break;}
        if(end<=68)throw new IllegalArgumentException("Native SID terminator");
        for(int i=end;i<body.length;i++)if(body[i]!=0)throw new IllegalArgumentException("Native SID padding");
        String sid=new String(body,68,end-68,StandardCharsets.UTF_16LE);
        if(!sid.matches("S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+"))throw new IllegalArgumentException("Native SID format");
        String[] parts=sid.split("-");for(int i=4;i<parts.length;i++)if(Long.parseLong(parts[i])>0xffffffffL)throw new IllegalArgumentException("Native SID component");
        return new Challenge(session,issued,expires,sid,Arrays.copyOfRange(body,36,68));
    }
    public static boolean current(byte[] body,long unixMilliseconds){
        Challenge c=read(body);long now=Math.addExact(unixMilliseconds,FILETIME_UNIX_MILLISECONDS);return now>=c.issued && now<c.expires;
    }
    public static byte[] sign(byte[] body,PrivateKey key)throws GeneralSecurityException{
        read(body);Signature signature=Signature.getInstance("SHA256withECDSA");signature.initSign(key);signature.update(body);return derToP1363(signature.sign());
    }
    public static boolean verify(byte[] body,byte[] signature,PublicKey key)throws GeneralSecurityException{
        read(body);if(signature==null || signature.length!=64)return false;
        try{Signature verifier=Signature.getInstance("SHA256withECDSA");verifier.initVerify(key);verifier.update(body);return verifier.verify(p1363ToDer(signature));}
        catch(IllegalArgumentException error){return false;}
    }
    public static byte[] derToP1363(byte[] der){
        if(der==null || der.length<8 || der.length>72 || der[0]!=0x30 || (der[1]&255)!=der.length-2)throw new IllegalArgumentException("ECDSA DER sequence");
        byte[] raw=new byte[64];int cursor=2;
        for(int item=0;item<2;item++){
            if(cursor+2>der.length || der[cursor++]!=2)throw new IllegalArgumentException("ECDSA DER integer");
            int length=der[cursor++]&255;
            if(length<1 || length>33 || cursor+length>der.length || (der[cursor]&128)!=0)throw new IllegalArgumentException("ECDSA DER integer size/sign");
            if(length>1 && der[cursor]==0){if((der[cursor+1]&128)==0)throw new IllegalArgumentException("ECDSA DER redundant zero");cursor++;length--;}
            if(length>32)throw new IllegalArgumentException("ECDSA integer overflow");
            System.arraycopy(der,cursor,raw,item*32+32-length,length);cursor+=length;
        }
        if(cursor!=der.length)throw new IllegalArgumentException("ECDSA DER trailing data");return raw;
    }
    public static byte[] p1363ToDer(byte[] raw){
        if(raw==null || raw.length!=64)throw new IllegalArgumentException("P1363 size");
        byte[][] integers=new byte[2][];int total=0;
        for(int i=0;i<2;i++){
            int start=i*32,end=start+32;while(start<end-1 && raw[start]==0)start++;
            int padding=(raw[start]&128)==0?0:1;integers[i]=new byte[end-start+padding];System.arraycopy(raw,start,integers[i],padding,end-start);total+=2+integers[i].length;
        }
        byte[] der=new byte[2+total];der[0]=0x30;der[1]=(byte)total;int cursor=2;
        for(byte[] integer:integers){der[cursor++]=2;der[cursor++]=(byte)integer.length;System.arraycopy(integer,0,der,cursor,integer.length);cursor+=integer.length;}return der;
    }
    private static ECParameterSpec p256()throws GeneralSecurityException{AlgorithmParameters parameters=AlgorithmParameters.getInstance("EC");parameters.init(new ECGenParameterSpec("secp256r1"));return parameters.getParameterSpec(ECParameterSpec.class);}
    public static byte[] exportNative(PublicKey key)throws GeneralSecurityException{
        if(!(key instanceof ECPublicKey))throw new InvalidKeyException("P256 public key required");
        ECPublicKey ec=(ECPublicKey)key;ECParameterSpec expected=p256();
        if(!ec.getParams().getOrder().equals(expected.getOrder()) || !ec.getParams().getGenerator().equals(expected.getGenerator()) || !ec.getParams().getCurve().equals(expected.getCurve()))throw new InvalidKeyException("Only P256");
        byte[] output=new byte[72];ByteBuffer.wrap(output).order(ByteOrder.LITTLE_ENDIAN).putInt(0x31534345).putInt(32);
        writeCoordinate(ec.getW().getAffineX(),output,8);writeCoordinate(ec.getW().getAffineY(),output,40);return output;
    }
    private static void writeCoordinate(BigInteger value,byte[] output,int at){byte[] bytes=value.toByteArray();int skip=bytes.length==33 && bytes[0]==0?1:0;if(value.signum()<0 || bytes.length-skip>32)throw new IllegalArgumentException("Coordinate");System.arraycopy(bytes,skip,output,at+32-bytes.length+skip,bytes.length-skip);}
    public static PublicKey importNative(byte[] bytes)throws GeneralSecurityException{
        if(bytes==null || bytes.length!=72)throw new InvalidKeyException("Native key size");ByteBuffer buffer=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        if(buffer.getInt()!=0x31534345 || buffer.getInt()!=32)throw new InvalidKeyException("Native key type");
        return KeyFactory.getInstance("EC").generatePublic(new ECPublicKeySpec(new ECPoint(new BigInteger(1,Arrays.copyOfRange(bytes,8,40)),new BigInteger(1,Arrays.copyOfRange(bytes,40,72))),p256()));
    }
}
