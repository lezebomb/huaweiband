using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using BandKey.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BandKey.Windows;

/// <summary>Connects only to BandKey's phone service. No band protocol or pairing API.</summary>
public sealed class BlePhoneTunnel(bool drainTargetAbort=false) : IBlePhoneConnection
{
    private long firstCompletedStream,lastCompletedStream;
    public TimeSpan CompletedStreamSpan{
        get{long first=Interlocked.Read(ref firstCompletedStream),last=Interlocked.Read(ref lastCompletedStream);
            return first>0 && last>=first?Stopwatch.GetElapsedTime(first,last):TimeSpan.Zero;}
    }
    public static readonly Guid ServiceId=new("ab7c1101-5d18-4e0b-9aba-30ad640be001"),RxId=new("ab7c1102-5d18-4e0b-9aba-30ad640be001"),TxId=new("ab7c1103-5d18-4e0b-9aba-30ad640be001");
    private readonly Channel<byte[]> received=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private BluetoothLEDevice? device;private GattDeviceService? service;private GattCharacteristic? rx,tx;
    private GattDeviceService[] discoveredServices=[];
    private GattSession? session;
    private GattDescriptor? healthDescriptor;
    private BlePeerBinding? peer;
    public BlePeerBinding? Peer=>Volatile.Read(ref disposed)!=0 || stop.IsCancellationRequested?null:Volatile.Read(ref peer);
    private GattClientCharacteristicConfigurationDescriptorValue subscription;
    public string DeliveryMode=>subscription==GattClientCharacteristicConfigurationDescriptorValue.Indicate?"indicate":subscription==GattClientCharacteristicConfigurationDescriptorValue.Notify?"notify":"none";
    internal static GattClientCharacteristicConfigurationDescriptorValue ChooseSubscription(GattCharacteristicProperties properties)=>
        properties.HasFlag(GattCharacteristicProperties.Indicate)?GattClientCharacteristicConfigurationDescriptorValue.Indicate:
        properties.HasFlag(GattCharacteristicProperties.Notify)?GattClientCharacteristicConfigurationDescriptorValue.Notify:
        throw new IOException("Phone downlink subscription unavailable");
    internal static bool SubscriptionMatches(byte[] bytes,GattClientCharacteristicConfigurationDescriptorValue expected)=>
        expected!=GattClientCharacteristicConfigurationDescriptorValue.None && bytes.Length==2 && bytes[0]==(byte)expected && bytes[1]==0;
    private long lastValue=Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource stop=new();
    private int disposed;
    private int started;
    private string? linkFailure;
    private long receivedBytes;
    private ushort lastMtu=23;
    public long SentBytes{get;private set;}public long ReceivedBytes=>Interlocked.Read(ref receivedBytes);public int Streams{get;private set;}
    public int AbortedStreams{get;private set;}
    public ushort Mtu{get{try{lastMtu=session?.MaxPduSize??lastMtu;}catch(Exception){/* The radio may disappear before final diagnostics. */}return lastMtu;}}
    public string Stage{get;private set;}="not-started";
    public BleTransportFailure? Failure{get;private set;}
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Create a new BLE tunnel before reconnecting");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stop.Token);budget.CancelAfter(TimeSpan.FromSeconds(45));var token=budget.Token;
        Stage="adapter";var adapter=await BluetoothAdapter.GetDefaultAsync().AsTask(token).WaitAsync(token);
        if(adapter==null || !adapter.IsCentralRoleSupported)throw new IOException("BLE central adapter unavailable");
        Stage="scan";var address=new TaskCompletionSource<(ulong Address,BluetoothAddressType Type)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received+=(_,e)=>{if(e.Advertisement.ServiceUuids.Contains(ServiceId))address.TrySetResult((e.BluetoothAddress,e.BluetoothAddressType));};
        watcher.Stopped+=(_,e)=>{if(e.Error!=BluetoothError.Success)address.TrySetException(new IOException("BLE scan failed"));};
        watcher.Start();(ulong Address,BluetoothAddressType Type) found;
        try{found=await address.Task.WaitAsync(TimeSpan.FromSeconds(20),token);}finally{watcher.Stop();}
        Stage="device";device=await BluetoothLEDevice.FromBluetoothAddressAsync(found.Address,found.Type).AsTask(token).WaitAsync(token)??throw new IOException("Phone unavailable");
        device.ConnectionStatusChanged+=OnConnection;
        // The Android process can recreate its GATT database with different ATT
        // handles. Refresh the primary-service view before selecting our UUID.
        Stage="service";var services=await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(token);
        // Keep the enumerated wrappers alive until this generation ends. Only
        // our service is inspected; cleanup must not invalidate an active session.
        discoveredServices=services.Services.ToArray();
        var matches=discoveredServices.Where(item=>item.Uuid==ServiceId).ToArray();
        if(services.Status!=GattCommunicationStatus.Success || matches.Length!=1)throw new IOException("BandKey phone service unavailable");
        service=matches[0];session=service.Session;session.SessionStatusChanged+=OnSession;session.MaintainConnection=true;
        var chars=await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(token);
        if(chars.Status!=GattCommunicationStatus.Success)throw new IOException("Phone characteristics unavailable");
        rx=chars.Characteristics.Single(c=>c.Uuid==RxId);tx=chars.Characteristics.Single(c=>c.Uuid==TxId);
        if(!rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))throw new IOException("Phone characteristic properties");
        subscription=ChooseSubscription(tx.CharacteristicProperties);
        tx.ValueChanged+=OnValue;Stage="subscribe";
        if(await tx.WriteClientCharacteristicConfigurationDescriptorAsync(subscription).AsTask(token).WaitAsync(token)!=GattCommunicationStatus.Success)throw new IOException("BLE subscription refused");
        Stage="health-descriptor";
        var descriptors=await tx.GetDescriptorsForUuidAsync(new Guid("00002902-0000-1000-8000-00805f9b34fb"),BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(token);
        if(descriptors.Status!=GattCommunicationStatus.Success || descriptors.Descriptors.Count!=1)throw new IOException("BLE health descriptor unavailable");
        healthDescriptor=descriptors.Descriptors[0];Interlocked.Exchange(ref lastValue,Stopwatch.GetTimestamp());
        token.ThrowIfCancellationRequested();
        if(session.SessionStatus==GattSessionStatus.Closed)throw new BleTransportException("gatt-session-closed");
        Volatile.Write(ref peer,new BlePeerBinding(Guid.NewGuid().ToString("N"),new(found.Address.ToString("X12"),found.Type.ToString()),DateTimeOffset.UtcNow));
        Stage="connected";
    }
    private void Cancel(){try{stop.Cancel();}catch(ObjectDisposedException){}}
    private void Fail(string code){Volatile.Write(ref peer,null);linkFailure=code;Failure??=BleTransportFailure.From(new BleTransportException(code));received.Writer.TryComplete(new BleTransportException(code));Cancel();}
    private void OnConnection(BluetoothLEDevice sender,object args)
    {
        try{if(Volatile.Read(ref disposed)==0 && sender.ConnectionStatus==BluetoothConnectionStatus.Disconnected)Fail("gatt-disconnected");}
        catch(Exception){Fail("connection-callback");}
    }
    private void OnSession(GattSession sender,GattSessionStatusChangedEventArgs args)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        if(args.Status==GattSessionStatus.Closed)Fail("gatt-session-closed");
    }
    private void OnValue(GattCharacteristic sender,GattValueChangedEventArgs args)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        try{
            var bytes=args.CharacteristicValue.ToArray();var frame=BlePipeFrame.Decode(bytes);
            Interlocked.Exchange(ref lastValue,Stopwatch.GetTimestamp());
            Interlocked.Add(ref receivedBytes,frame.Payload.Length);
            if(!received.Writer.TryWrite(bytes)){Interlocked.Add(ref receivedBytes,-frame.Payload.Length);Fail("receive-queue-limit");}
        }catch(Exception){Fail("receive-frame-invalid");}
    }
    private Task<BlePipeFrame> ReadAsync(CancellationToken token)=>ReadFrameAsync(received.Reader,token);
    internal static async Task<BlePipeFrame> ReadFrameAsync(ChannelReader<byte[]> frames,CancellationToken token)
    {
        try{return BlePipeFrame.Decode(await frames.ReadAsync(token));}
        // Channel completion wins races with cancellation. Preserve the actual BLE failure
        // as an IOException so the transport supervisor can apply its retry policy.
        catch(ChannelClosedException error)when(error.InnerException is BleTransportException reason){throw new BleTransportException(reason.Code,error);}
        catch(ChannelClosedException)when(token.IsCancellationRequested){throw new OperationCanceledException(token);}
    }
    private async Task ProbeIdleAsync(CancellationToken token)
    {
        // An uncached descriptor read checks the phone, not Windows' remembered subscription.
        var result=await healthDescriptor!.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token).WaitAsync(token);
        if(result.Status==GattCommunicationStatus.AccessDenied)throw new BleTransportException("gatt-permission-denied");
        if(result.Status!=GattCommunicationStatus.Success)throw new BleTransportException("gatt-health-"+result.Status.ToString().ToLowerInvariant());
        var bytes=result.Value.ToArray();
        if(!SubscriptionMatches(bytes,subscription))throw new BleTransportException("gatt-idle-subscription-lost");
    }
    private async Task WriteAsync(BlePipeFrame frame,CancellationToken token)
    {
        byte[] bytes=frame.Encode();
        // Native cancellation can finish late. Bound our wait independently so
        // the supervisor can dispose this generation and reconnect on schedule.
        var result=await BleGattOperation.WriteAsync(stop=>rx!.WriteValueAsync(bytes.AsBuffer(),GattWriteOption.WriteWithResponse).AsTask(stop),token);
        if(result!=GattCommunicationStatus.Success)throw new BleTransportException("gatt-write-"+result.ToString().ToLowerInvariant());
        SentBytes+=frame.Payload.Length;
    }
    public async Task RunAsync(IPAddress target,int port,TimeSpan duration,string? readyFile=null,string? stopFile=null,CancellationToken cancellation=default)
    {
        if(!LoopbackTcpTunnel.IsLocalTarget(target) || port is <1 or >65535 || duration<=TimeSpan.Zero || duration>TimeSpan.FromHours(2))throw new ArgumentException("BLE target/duration");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stop.Token);lifetime.CancelAfter(duration);var token=lifetime.Token;
        try{await ConnectAsync(token);}
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception error)
        {
            string code=error is UnauthorizedAccessException?"gatt-permission-denied":"connect-"+Stage;
            Failure??=BleTransportFailure.From(error,code);throw new BleTransportException(code,error);
        }
        if(readyFile!=null)File.WriteAllText(readyFile,"connected");
        // A stop file is polled independently so a quiet BLE connection can stop promptly.
        async Task WatchStop(){while(!token.IsCancellationRequested){if(stopFile!=null && File.Exists(stopFile)){lifetime.Cancel();return;}try{await Task.Delay(250,token);}catch(OperationCanceledException){return;}}}
        var watching=WatchStop();
        async Task WatchHealth()
        {
            // A phone process may disappear without a Windows disconnect event
            // while an opaque stream is still open. Probe a silent active stream
            // too, rather than waiting for its full 45-second lifetime to expire.
            try{await BleIdleWatchdog.RunAsync(()=>(Stage is "waiting-stream" or "forwarding") && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastValue))>=TimeSpan.FromSeconds(10),ProbeIdleAsync,token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){}
            catch(BleTransportException error){Fail(error.Code);}
        }
        var health=WatchHealth();
        try
        {
            while(!token.IsCancellationRequested)
            {
                Stage="waiting-stream";var open=await ReadAsync(token);
                try
                {
                    Stage="forwarding";
                    var stream=await BleStreamForwarder.RunAsync(open,target,port,ReadAsync,WriteAsync,()=>Math.Clamp(Mtu-3-BlePipeFrame.Header,1,BlePipeFrame.MaxPayload),token,drainTargetAbort:drainTargetAbort);
                    if(stream.TargetAborted){AbortedStreams++;continue;}
                    long completed=Stopwatch.GetTimestamp();Interlocked.CompareExchange(ref firstCompletedStream,completed,0);Interlocked.Exchange(ref lastCompletedStream,completed);
                    Streams++;
                }
                catch(Exception error)when(error is IOException or SocketException or OperationCanceledException or ArgumentException)
                {
                    // The peer must reconnect after an ambiguous stream failure; queued
                    // data can never be interpreted as a new TLS session.
                    Stage="stream-failed";
                    if(error is not OperationCanceledException || !token.IsCancellationRequested)Failure??=BleTransportFailure.From(error);
                    throw;
                }
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){if(linkFailure!=null){Stage="link-failed";throw new BleTransportException(linkFailure);}Stage="stopped";}
        catch(Exception error){Failure??=BleTransportFailure.From(error);throw;}
        finally{Volatile.Write(ref peer,null);lifetime.Cancel();await Task.WhenAll(watching,health);}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Volatile.Write(ref peer,null);
        stop.Cancel();received.Writer.TryComplete();
        try{if(tx!=null)tx.ValueChanged-=OnValue;}catch(Exception){/* Already-closed WinRT object. */}
        try{if(device!=null)device.ConnectionStatusChanged-=OnConnection;}catch(Exception){}
        try{if(session!=null)session.SessionStatusChanged-=OnSession;}catch(Exception){}
        try{if(tx!=null){using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(2));await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(budget.Token).WaitAsync(budget.Token);}}catch(Exception){/* A removed radio cannot acknowledge unsubscribe. */}
        try{if(session!=null)session.MaintainConnection=false;}catch(Exception){}
        foreach(var item in discoveredServices)try{item.Dispose();}catch(Exception){}
        discoveredServices=[];service=null;rx=null;tx=null;healthDescriptor=null;
        try{session?.Dispose();}catch(Exception){}session=null;
        try{device?.Dispose();}catch(Exception){}device=null;
        stop.Dispose();
    }
}
