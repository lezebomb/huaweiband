namespace BandKey.Core;

// Observations describe radio evidence, never cryptographic authorization.
public sealed record AdvertisementData(int Type, string Hex);
public sealed record DeviceObservation(string Address, string Name, int Rssi, DateTimeOffset SeenAt,
    string AddressType, string[] Services, string AdvertisementType = "Unknown", bool Connectable = false,
    bool Scannable = false, bool ScanResponse = false, bool Anonymous = false,
    AdvertisementData[]? Data = null, string Transport = "windows-ble");

public interface IDeviceObservationSource : IDisposable
{
    event Action<DeviceObservation>? Observed;
    event Action<string>? Failed;
    bool Healthy { get; }
    void Start();
}

public sealed record ObservationTarget(string Address, string? AddressType)
{
    public bool Matches(DeviceObservation observation) => !observation.Anonymous &&
        Address.Equals(observation.Address, StringComparison.OrdinalIgnoreCase) &&
        (AddressType == null || AddressType.Equals(observation.AddressType, StringComparison.OrdinalIgnoreCase));
}
