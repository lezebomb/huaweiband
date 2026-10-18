using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using BandKey.Core;

namespace BandKey.Windows;

public sealed record AdapterReport(bool Available, string? Name, string? RadioState, bool LowEnergySupported, string? Error);

public sealed class BleScanner : IDeviceObservationSource
{
    private readonly ConcurrentDictionary<string, DeviceObservation> devices = new();
    private BluetoothLEAdvertisementWatcher? watcher;
    public event Action<DeviceObservation>? Observed;
    public event Action<string>? Failed;
    public bool Healthy => watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public DeviceObservation[] Devices => devices.Values.OrderByDescending(d => d.Rssi).ToArray();
    public void Start()
    {
        DisposeWatcher();
        // Passive scan: no GATT writes, pairing, authentication, scan requests or device settings.
        watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        watcher.Start();
    }
    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (!ReferenceEquals(sender, watcher)) return; // Ignore callbacks queued by a replaced watcher.
        var address = args.BluetoothAddress.ToString("X12");
        var name = args.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name) && devices.TryGetValue(address, out var old)) name = old.Name;
        var sections = args.Advertisement.DataSections.Take(16).Select(section =>
        {
            using var reader = DataReader.FromBuffer(section.Data);
            var bytes = new byte[Math.Min(reader.UnconsumedBufferLength, 256)]; reader.ReadBytes(bytes);
            return new AdvertisementData(section.DataType, Convert.ToHexString(bytes));
        }).ToArray();
        var item = new DeviceObservation(address, name ?? "", args.RawSignalStrengthInDBm, args.Timestamp,
            args.BluetoothAddressType.ToString(), args.Advertisement.ServiceUuids.Select(s => s.ToString()).ToArray(),
            args.AdvertisementType.ToString(), args.IsConnectable, args.IsScannable, args.IsScanResponse, args.IsAnonymous, sections);
        if (devices.Count >= 256 && !devices.ContainsKey(address))
        {
            var oldest = devices.Values.MinBy(x => x.SeenAt);
            if (oldest != null) devices.TryRemove(oldest.Address, out _);
        }
        devices[address] = item; Observed?.Invoke(item);
    }
    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    { if (ReferenceEquals(sender, watcher)) Failed?.Invoke(args.Error.ToString()); }
    public static async Task<AdapterReport> ProbeAdapterAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return new(false, null, null, false, "No default Bluetooth adapter");
            var radio = await adapter.GetRadioAsync();
            return new(true, radio?.Name, radio?.State.ToString(), adapter.IsLowEnergySupported, null);
        }
        catch (Exception e) { return new(false, null, null, false, e.GetType().Name + ": " + e.Message); }
    }
    private void DisposeWatcher()
    {
        if (watcher == null) return;
        watcher.Received -= OnReceived; watcher.Stopped -= OnStopped;
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        watcher = null;
    }
    public void Dispose() => DisposeWatcher();
}
