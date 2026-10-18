using System.Text;
using System.Text.Json;
using BandKey.Core;

namespace BandKey.Windows;

public sealed class ObservationLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private long bytes;
    public int Count { get; private set; }
    public bool Truncated { get; private set; }
    public string? Error { get; private set; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ObservationLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new(path, false, new UTF8Encoding(false));
    }
    public void Record(DeviceObservation observation)
    {
        lock (gate)
        {
            if (Truncated || Error != null) return;
            var line = JsonSerializer.Serialize(observation, Json);
            var size = Encoding.UTF8.GetByteCount(line) + 2;
            if (Count >= 50000 || bytes + size > 16_777_216) { Truncated = true; return; }
            try { writer.WriteLine(line); Count++; bytes += size; }
            catch (IOException e) { Error = e.GetType().Name; }
        }
    }
    public void Dispose() { lock (gate) writer.Dispose(); }
}
