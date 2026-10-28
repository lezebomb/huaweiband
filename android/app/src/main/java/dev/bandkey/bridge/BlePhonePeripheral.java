package dev.bandkey.bridge;

import android.annotation.SuppressLint;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.Context;
import android.os.*;
import java.io.*;
import java.net.*;
import java.security.SecureRandom;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicBoolean;

/** BLE pipe owned by a visible page or an explicitly started foreground service. */
@SuppressLint("MissingPermission")
public final class BlePhonePeripheral implements AutoCloseable {
    public static final UUID SERVICE=UUID.fromString("ab7c1101-5d18-4e0b-9aba-30ad640be001"),RX=UUID.fromString("ab7c1102-5d18-4e0b-9aba-30ad640be001"),TX=UUID.fromString("ab7c1103-5d18-4e0b-9aba-30ad640be001"),CCCD=UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private final Context context;private final int port;private final java.util.function.Consumer<String> status;
    private final java.util.function.BooleanSupplier allowed;
    private final AtomicBoolean closing=new AtomicBoolean();
    private final CountDownLatch advertising=new CountDownLatch(1);
    private final Semaphore notification=new Semaphore(0);
    private final ArrayBlockingQueue<BlePipeFrame> incoming=new ArrayBlockingQueue<>(128);
    private final Object streamState=new Object();
    private final ExecutorService workers=Executors.newCachedThreadPool();
    private final ScheduledExecutorService deadlines=Executors.newSingleThreadScheduledExecutor();
    private volatile boolean closed,advertised;private volatile int subscription;private volatile String failure="";private volatile int mtu=23;
    private volatile BluetoothDevice peer;private BluetoothGattServer gatt;private BluetoothLeAdvertiser advertiser;private BluetoothGattCharacteristic tx;
    private ServerSocket listener;private volatile Socket socket;private volatile long activeSession;
    public volatile long sentBytes,receivedBytes;public volatile int connections,localSocketAborts;
    public BlePhonePeripheral(Context context,int port,java.util.function.Consumer<String> status){this(context,port,status,()->true);}
    public BlePhonePeripheral(Context context,int port,java.util.function.Consumer<String> status,java.util.function.BooleanSupplier allowed){this.context=context.getApplicationContext();this.port=port;this.status=status;this.allowed=allowed;}
    private boolean available(){if(closed)return false;if(!allowed.getAsBoolean()){close();return false;}return true;}
    public void start()throws Exception{
        BluetoothManager manager=(BluetoothManager)context.getSystemService(Context.BLUETOOTH_SERVICE);BluetoothAdapter adapter=manager.getAdapter();
        if(adapter==null || !adapter.isEnabled() || !adapter.isMultipleAdvertisementSupported())throw new IOException("BLE advertising unavailable");
        advertiser=adapter.getBluetoothLeAdvertiser();if(advertiser==null)throw new IOException("BLE advertiser unavailable");
        listener=new ServerSocket();listener.setReuseAddress(true);listener.bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"),port),1);listener.setSoTimeout(1000);
        gatt=manager.openGattServer(context,callback);if(gatt==null)throw new IOException("GATT server unavailable");
        BluetoothGattService service=new BluetoothGattService(SERVICE,BluetoothGattService.SERVICE_TYPE_PRIMARY);
        service.addCharacteristic(new BluetoothGattCharacteristic(RX,BluetoothGattCharacteristic.PROPERTY_WRITE,BluetoothGattCharacteristic.PERMISSION_WRITE));
        tx=new BluetoothGattCharacteristic(TX,BluetoothGattCharacteristic.PROPERTY_NOTIFY|BluetoothGattCharacteristic.PROPERTY_INDICATE,0);tx.addDescriptor(new BluetoothGattDescriptor(CCCD,BluetoothGattDescriptor.PERMISSION_READ|BluetoothGattDescriptor.PERMISSION_WRITE));service.addCharacteristic(tx);
        if(!gatt.addService(service))throw new IOException("GATT service registration failed");
        workers.execute(this::acceptLoop);
    }
    public boolean awaitAdvertising(long milliseconds)throws InterruptedException{return advertising.await(milliseconds,TimeUnit.MILLISECONDS) && failure.isEmpty() && !closed;}
    public boolean isAdvertising(){return advertised && !closed;}
    public boolean isConnected(){return peer!=null && subscription!=BleSubscription.DISABLED && !closed;}
    public boolean isStreamActive(){synchronized(streamState){return activeSession!=0;}}
    public String failure(){return failure;}
    private void problem(String code){failure=code;status.accept(code);closeSocket();}
    private void closeSocket(){Socket current=socket;if(current!=null)try{current.close();}catch(IOException ignored){};}
    private void recordStreamFailure(Exception error){
        try{
            org.json.JSONArray causes=new org.json.JSONArray();Throwable cause=error;
            for(int depth=0;cause!=null && depth<8;depth++,cause=cause.getCause()){
                org.json.JSONObject entry=new org.json.JSONObject().put("type",cause.getClass().getSimpleName());
                String detail=cause.getMessage();if(detail!=null)entry.put("detail",detail.substring(0,Math.min(detail.length(),256)));causes.put(entry);
            }
            org.json.JSONObject report=new org.json.JSONObject().put("recordedAt",System.currentTimeMillis()).put("causes",causes).put("streams",connections).put("mtu",mtu);
            try(OutputStream out=context.openFileOutput("vm-ble-stream-error.json",Context.MODE_PRIVATE)){out.write(report.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));}
        }catch(Exception ignored){}
    }
    private final AdvertiseCallback advertisement=new AdvertiseCallback(){
        @Override public void onStartSuccess(AdvertiseSettings settings){if(!available()){try{advertiser.stopAdvertising(this);}catch(RuntimeException ignored){}return;}advertised=true;advertising.countDown();status.accept("BLE 广播已启动，等待电脑连接；TLS 身份核验仍必需。");}
        @Override public void onStartFailure(int code){failure="BLE advertise failed: "+code;advertising.countDown();}
    };
    private final BluetoothGattServerCallback callback=new BluetoothGattServerCallback(){
        @Override public void onServiceAdded(int result,BluetoothGattService service){
            if(!available())return;if(result!=BluetoothGatt.GATT_SUCCESS){failure="GATT service failed";advertising.countDown();return;}
            try{advertiser.startAdvertising(new AdvertiseSettings.Builder().setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY).setConnectable(true).setTimeout(0).build(),new AdvertiseData.Builder().setIncludeDeviceName(false).addServiceUuid(new ParcelUuid(SERVICE)).build(),advertisement);}catch(RuntimeException error){failure="BLE advertising stopped: "+error.getClass().getSimpleName();advertising.countDown();}
        }
        @Override public void onConnectionStateChange(BluetoothDevice device,int result,int state){
            if(!available())return;
            if(state==BluetoothProfile.STATE_CONNECTED){if(peer!=null && !peer.equals(device)){gatt.cancelConnection(device);return;}peer=device;mtu=23;status.accept("电脑 GATT 已连接，尚未验证 TLS 身份。");}
            else if(device.equals(peer)){subscription=BleSubscription.DISABLED;peer=null;problem("BLE disconnected; pending stream cancelled");}
        }
        @Override public void onMtuChanged(BluetoothDevice device,int value){if(available() && device.equals(peer))mtu=Math.max(23,value);}
        @Override public void onDescriptorReadRequest(BluetoothDevice device,int request,int offset,BluetoothGattDescriptor descriptor){if(!available())return;gatt.sendResponse(device,request,device.equals(peer) && offset==0 && CCCD.equals(descriptor.getUuid())?BluetoothGatt.GATT_SUCCESS:BluetoothGatt.GATT_FAILURE,offset,BleSubscription.encode(subscription));}
        @Override public void onDescriptorWriteRequest(BluetoothDevice device,int request,BluetoothGattDescriptor descriptor,boolean prepared,boolean response,int offset,byte[] value){
            if(!available())return;
            int mode=BleSubscription.parse(value);
            boolean okay=device.equals(peer) && CCCD.equals(descriptor.getUuid()) && !prepared && offset==0 && mode>=0;
            if(okay){int previous=subscription;subscription=mode;if(mode==BleSubscription.DISABLED || (mode!=previous && isStreamActive()))closeSocket();}
            if(response)gatt.sendResponse(device,request,okay?BluetoothGatt.GATT_SUCCESS:BluetoothGatt.GATT_FAILURE,0,null);
        }
        @Override public void onCharacteristicWriteRequest(BluetoothDevice device,int request,BluetoothGattCharacteristic characteristic,boolean prepared,boolean response,int offset,byte[] value){
            if(!available())return;
            boolean okay=false;
            synchronized(streamState){
                try{BlePipeFrame frame=BlePipeFrame.decode(value);okay=device.equals(peer) && subscription!=BleSubscription.DISABLED && RX.equals(characteristic.getUuid()) && response && !prepared && offset==0 && activeSession!=0 && frame.session==activeSession && incoming.offer(frame);if(okay)receivedBytes+=frame.payload.length;}catch(IllegalArgumentException ignored){}
                if(response)gatt.sendResponse(device,request,okay?BluetoothGatt.GATT_SUCCESS:BluetoothGatt.GATT_FAILURE,0,null);
            }
            if(!okay && device.equals(peer))problem("BLE frame or queue rejected");
        }
        @Override public void onNotificationSent(BluetoothDevice device,int result){if(available() && device.equals(peer)){if(result!=BluetoothGatt.GATT_SUCCESS)problem("BLE notification failed");notification.release();}}
    };
    private void send(int kind,long id,AtomicInteger sequence,byte[] payload)throws Exception{
        BluetoothDevice target=peer;int mode=subscription;if(!available() || mode==BleSubscription.DISABLED || target==null)throw new IOException("BLE peer unavailable");
        byte[] value=new BlePipeFrame(kind,id,sequence.getAndIncrement(),payload).encode();notification.drainPermits();
        boolean queued;
        if(Build.VERSION.SDK_INT>=33)queued=gatt.notifyCharacteristicChanged(target,tx,mode==BleSubscription.INDICATE,value)==BluetoothStatusCodes.SUCCESS;
        else{tx.setValue(value);queued=gatt.notifyCharacteristicChanged(target,tx,mode==BleSubscription.INDICATE);}
        if(!queued || !notification.tryAcquire(3,TimeUnit.SECONDS) || !failure.isEmpty())throw new IOException("BLE notification timeout/failure");sentBytes+=payload.length;
    }
    private void acceptLoop(){
        while(available()){
            try{Socket client=listener.accept();socket=client;serve(client);}catch(SocketTimeoutException ignored){}catch(Exception error){if(!closed){recordStreamFailure(error);problem("BLE stream ended: "+error.getClass().getSimpleName()+"; reconnect required");subscription=BleSubscription.DISABLED;BluetoothDevice target=peer;if(target!=null)try{gatt.cancelConnection(target);}catch(RuntimeException ignored){}}}
            finally{closeSocket();socket=null;synchronized(streamState){activeSession=0;incoming.clear();}}
        }
    }
    private void serve(Socket client)throws Exception{
        if(!available() || subscription==BleSubscription.DISABLED || peer==null){client.close();return;}failure="";client.setSoTimeout(1000);client.setTcpNoDelay(true);
        long random;do{random=new SecureRandom().nextLong();}while(random==0);final long id=random;synchronized(streamState){activeSession=id;incoming.clear();}AtomicInteger sequence=new AtomicInteger();
        BlePipeFrame.Receiver receiver=new BlePipeFrame.Receiver(id,BlePipeFrame.READY);send(BlePipeFrame.OPEN,id,sequence,new byte[0]);
        BlePipeFrame ready=incoming.poll(8,TimeUnit.SECONDS);if(ready==null)throw new IOException("BLE remote ready timeout");receiver.accept(ready);connections++;
        long deadline=SystemClock.elapsedRealtime()+45000;
        // A socket write can block even when SO_TIMEOUT bounds reads. Closing it
        // from an independent timer enforces the lifetime in both directions.
        ScheduledFuture<?> expiry=deadlines.schedule(()->{try{client.close();}catch(IOException ignored){}},45,TimeUnit.SECONDS);
        try{
            if(BleSocketRelay.run(client,receiver,incoming,(kind,payload)->send(kind,id,sequence,payload),
                ()->Math.min(BlePipeFrame.MAX_PAYLOAD,Math.max(1,mtu-3-BlePipeFrame.HEADER)),this::available,SystemClock::elapsedRealtime,deadline,workers))localSocketAborts++;
        }finally{expiry.cancel(false);}
    }
    @Override public void close(){if(!closing.compareAndSet(false,true))return;closed=true;subscription=BleSubscription.DISABLED;closeSocket();if(listener!=null)try{listener.close();}catch(IOException ignored){}try{if(advertiser!=null)advertiser.stopAdvertising(advertisement);}catch(RuntimeException ignored){}try{if(gatt!=null && peer!=null)gatt.cancelConnection(peer);}catch(RuntimeException ignored){}try{if(gatt!=null)gatt.close();}catch(RuntimeException ignored){}peer=null;workers.shutdownNow();deadlines.shutdownNow();}
}
