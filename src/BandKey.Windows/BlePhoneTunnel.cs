using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using BandKey.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BandKey.Windows;

/// <summary>Connects only to BandKey's phone service. No band protocol or pairing API.</summary>
public sealed class BlePhoneTunnel : IBlePhoneConnection
{
    public static readonly Guid ServiceId=new("ab7c1101-5d18-4e0b-9aba-30ad640be001"),RxId=new("ab7c1102-5d18-4e0b-9aba-30ad640be001"),TxId=new("ab7c1103-5d18-4e0b-9aba-30ad640be001");
    private readonly Channel<byte[]> received=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128){SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private BluetoothLEDevice? device;private GattDeviceService? service;private GattCharacteristic? rx,tx;
    private readonly CancellationTokenSource stop=new();
    private int disposed;
    private int started;
    private string? linkFailure;
    private long receivedBytes;
    private ushort lastMtu=23;
    public long SentBytes{get;private set;}public long ReceivedBytes=>Interlocked.Read(ref receivedBytes);public int Streams{get;private set;}
    public ushort Mtu{get{try{lastMtu=service?.Session.MaxPduSize??lastMtu;}catch(Exception){/* The radio may disappear before final diagnostics. */}return lastMtu;}}
    public string Stage{get;private set;}="not-started";
    public BleTransportFailure? Failure{get;private set;}
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Create a new BLE tunnel before reconnecting");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stop.Token);budget.CancelAfter(TimeSpan.FromSeconds(45));var token=budget.Token;
        Stage="adapter";var adapter=await BluetoothAdapter.GetDefaultAsync().AsTask(token);
        if(adapter==null || !adapter.IsCentralRoleSupported)throw new IOException("BLE central adapter unavailable");
        Stage="scan";var address=new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceId);
        watcher.Received+=(_,e)=>{if(e.Advertisement.ServiceUuids.Contains(ServiceId))address.TrySetResult(e.BluetoothAddress);};
        watcher.Stopped+=(_,e)=>{if(e.Error!=BluetoothError.Success)address.TrySetException(new IOException("BLE scan failed"));};
        watcher.Start();ulong found;
        try{found=await address.Task.WaitAsync(TimeSpan.FromSeconds(20),token);}finally{watcher.Stop();}
        Stage="device";device=await BluetoothLEDevice.FromBluetoothAddressAsync(found).AsTask(token)??throw new IOException("Phone unavailable");
        device.ConnectionStatusChanged+=OnConnection;
        Stage="service";var services=await device.GetGattServicesForUuidAsync(ServiceId,BluetoothCacheMode.Uncached).AsTask(token);
        if(services.Status!=GattCommunicationStatus.Success || services.Services.Count!=1){foreach(var item in services.Services)item.Dispose();throw new IOException("BandKey phone service unavailable");}
        service=services.Services[0];service.Session.MaintainConnection=true;
        var chars=await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
        if(chars.Status!=GattCommunicationStatus.Success)throw new IOException("Phone characteristics unavailable");
        rx=chars.Characteristics.Single(c=>c.Uuid==RxId);tx=chars.Characteristics.Single(c=>c.Uuid==TxId);
        if(!rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) || !tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))throw new IOException("Phone characteristic properties");
        tx.ValueChanged+=OnValue;Stage="subscribe";
        if(await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token)!=GattCommunicationStatus.Success)throw new IOException("BLE subscription refused");
        Stage="connected";
    }
    private void Cancel(){try{stop.Cancel();}catch(ObjectDisposedException){}}
    private void Fail(string code){linkFailure=code;Failure??=BleTransportFailure.From(new BleTransportException(code));received.Writer.TryComplete(new BleTransportException(code));Cancel();}
    private void OnConnection(BluetoothLEDevice sender,object args)
    {
        try{if(Volatile.Read(ref disposed)==0 && sender.ConnectionStatus==BluetoothConnectionStatus.Disconnected)Fail("gatt-disconnected");}
        catch(Exception){Fail("connection-callback");}
    }
    private void OnValue(GattCharacteristic sender,GattValueChangedEventArgs args)
    {
        if(Volatile.Read(ref disposed)!=0)return;
        try{
            var bytes=args.CharacteristicValue.ToArray();var frame=BlePipeFrame.Decode(bytes);
            Interlocked.Add(ref receivedBytes,frame.Payload.Length);
            if(!received.Writer.TryWrite(bytes)){Interlocked.Add(ref receivedBytes,-frame.Payload.Length);Fail("receive-queue-limit");}
        }catch(Exception){Fail("receive-frame-invalid");}
    }
    private async Task<BlePipeFrame> ReadAsync(CancellationToken token)=>BlePipeFrame.Decode(await received.Reader.ReadAsync(token));
    private async Task WriteAsync(BlePipeFrame frame,CancellationToken token)
    {
        byte[] bytes=frame.Encode();using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(5));
        GattCommunicationStatus result;
        try{result=await rx!.WriteValueAsync(bytes.AsBuffer(),GattWriteOption.WriteWithResponse).AsTask(budget.Token);}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new BleTransportException("gatt-write-timeout");}
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
        try
        {
            while(!token.IsCancellationRequested)
            {
                Stage="waiting-stream";var open=await ReadAsync(token);
                try
                {
                    Stage="forwarding";
                    await BleStreamForwarder.RunAsync(open,target,port,ReadAsync,WriteAsync,()=>Math.Clamp(Mtu-3-BlePipeFrame.Header,1,BlePipeFrame.MaxPayload),token);
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
        finally{lifetime.Cancel();await watching;}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();received.Writer.TryComplete();if(tx!=null)tx.ValueChanged-=OnValue;if(device!=null)device.ConnectionStatusChanged-=OnConnection;
        try{if(tx!=null){using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(2));await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(budget.Token);}}catch(Exception){/* A removed radio cannot acknowledge unsubscribe. */}
        try{if(service!=null)service.Session.MaintainConnection=false;}catch(Exception){}
        try{service?.Dispose();}catch(Exception){}try{device?.Dispose();}catch(Exception){}
        stop.Dispose();
    }
}
