using System.Text.Json;

namespace BandKey.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--test-flow"))
        {
            var output = Path.GetFullPath(Arg(args, "--output") ?? "flow-lab.json");
            try { Environment.ExitCode = FlowLab.RunAsync(output).GetAwaiter().GetResult(); }
            catch (Exception e)
            {
                Environment.ExitCode = 2;
                // CLI diagnostics must fail without opening a crash dialog or exposing secrets.
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.WriteAllText(output, JsonSerializer.Serialize(new { simulated = true, failed = 1,
                        startupError = e.GetType().Name, hresult = e.HResult, realWindowsAuthenticationTested = false, realWearableTested = false }, Store.Json));
                }
                catch (Exception) { /* Output may itself be inaccessible. The exit code still records failure. */ }
            }
            return;
        }
        if (args.Contains("--probe")) { ProbeAsync(args).GetAwaiter().GetResult(); return; }
        if (args.Contains("--probe-classic"))
        {
            var report = ClassicProbe.Scan(!args.Contains("--cache-only"), int.TryParse(Arg(args, "--seconds"), out var classicSeconds) ? classicSeconds : 8);
            var output = Arg(args, "--output") ?? "classic-probe.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, Store.Json));
            Environment.ExitCode = report.NativeError is 0 or 259 ? 0 : 2; return;
        }
        if (args.Contains("--inspect-gatt"))
        {
            try
            {
                var report = GattProbe.InspectAsync(Arg(args, "--address") ?? "").GetAwaiter().GetResult();
                File.WriteAllText(Arg(args, "--output") ?? "gatt-probe.json", JsonSerializer.Serialize(report, Store.Json));
            }
            catch (Exception e)
            {
                File.WriteAllText(Arg(args, "--output") ?? "gatt-probe.json", JsonSerializer.Serialize(new { error = e.GetType().Name + ": " + e.Message }, Store.Json));
                Environment.ExitCode = 2;
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        var data = Arg(args, "--data") ?? Path.Combine(AppContext.BaseDirectory, "runtime");
        if (args.Contains("--render-ui"))
        {
            var agent = new Agent(new Store(data));
            using (var form = new MainForm(agent, false, preview: true))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Arg(args, "--output") ?? "ui-preview.png", System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            }
            agent.DisposeAsync().AsTask().GetAwaiter().GetResult(); return;
        }
        // One process per data directory prevents two agents racing the same identity and lock policy.
        using var mutex = new Mutex(true, "Local\\BandKey-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToUpperInvariant())))[..24], out var first);
        if (!first) { MessageBox.Show("BandKey 已在运行，请使用系统托盘图标。"); return; }
        try { Application.Run(new MainForm(new Agent(new Store(data)), args.Contains("--tray"))); }
        catch (Exception e) { MessageBox.Show("BandKey 启动失败：" + e.Message, "BandKey"); Environment.ExitCode = 1; }
    }
    private static async Task ProbeAsync(string[] args)
    {
        var output = Arg(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "probe.json");
        var seconds = int.TryParse(Arg(args, "--seconds"), out var value) ? Math.Clamp(value, 1, 300) : 20;
        var adapter = await BleScanner.ProbeAdapterAsync();
        using var scanner = new BleScanner(); var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var observations = Arg(args, "--jsonl") is string logPath ? new ObservationLog(logPath) : null;
        if (observations != null) scanner.Observed += observations.Record;
        scanner.Failed += errors.Add;
        try { scanner.Start(); await Task.Delay(TimeSpan.FromSeconds(seconds)); }
        catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        var report = new { generatedAt = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString,
            accountSource = "See environment report; no account name exported", sessionLocked = WindowsSession.IsLocked(), adapter,
            scannerHealthy = scanner.Healthy, seconds, errors = errors.ToArray(), devices = scanner.Devices,
            observationLog = observations == null ? null : new { observations.Count, observations.Truncated, observations.Error } };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Store.Json));
        Environment.ExitCode = adapter.Available && scanner.Healthy ? 0 : 2;
    }
    private static string? Arg(string[] args, string flag) { var index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}
