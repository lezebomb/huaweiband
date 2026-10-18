using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BandKey.Windows;

public static class GattProbe
{
    public static async Task<object> InspectAsync(string address)
    {
        if (address.Length != 12 || !ulong.TryParse(address, System.Globalization.NumberStyles.HexNumber, null, out var value))
            throw new ArgumentException("Use 12 hex digits for BLE address");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(value).AsTask(timeout.Token);
        if (device == null) return new { address, error = "Device unavailable or access denied" };
        var access = await device.RequestAccessAsync().AsTask(timeout.Token);
        if (access != DeviceAccessStatus.Allowed) return new { address, error = "Device access: " + access };
        var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
        var services = new List<object>();
        foreach (var service in result.Services)
        {
            using (service)
            {
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
                services.Add(new { uuid = service.Uuid, status = chars.Status.ToString(),
                    characteristics = chars.Characteristics.Select(c => new { uuid = c.Uuid, properties = c.CharacteristicProperties.ToString() }).ToArray() });
            }
        }
        return new { address, name = device.Name, connectionStatus = device.ConnectionStatus.ToString(),
            status = result.Status.ToString(), services, protocolAuthenticated = false, writesPerformed = 0 };
    }
}
