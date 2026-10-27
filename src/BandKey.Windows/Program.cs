using System.Text.Json;

namespace BandKey.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if(args.Contains("--host-audit"))
        {
            try{
                var report=Task.Run(HostReadinessAudit.CaptureAsync).GetAwaiter().GetResult();
                File.WriteAllText(Arg(args,"--output")??"host-readiness.json",JsonSerializer.Serialize(report,Store.Json));
            }catch(Exception){Environment.ExitCode=2;}
            return;
        }
        if(args.Contains("--ble-bridge") || args.Contains("--render-ble-settings"))
        {
            ApplicationConfiguration.Initialize();
            try{
                var output=Arg(args,"--output");var bleData=Arg(args,"--data")??Path.Combine(AppContext.BaseDirectory,"runtime","ble-bridge");
                using var form=new BleBridgeForm(new Store(bleData));
                if(args.Contains("--render-ble-settings")){
                    if(output==null)throw new ArgumentException("Missing render output");form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();form.PerformLayout();var surface=form.Controls[0];using var bitmap=new Bitmap(surface.Width,surface.Height);surface.DrawToBitmap(bitmap,new Rectangle(Point.Empty,surface.Size));bitmap.Save(output);form.Close();
                }else Application.Run(form);
            }catch(Exception){Environment.ExitCode=2;}
            return;
        }
        if(args.Contains("--ble-tunnel"))
        {
            var output=Arg(args,"--output");
            RunBleTunnel(args,output).GetAwaiter().GetResult();return;
        }
        if(args.Contains("--usb-tunnel"))
        {
            try
            {
                var address=System.Net.IPAddress.Parse(Arg(args,"--target")??throw new ArgumentException("Missing target"));
                int seconds=int.TryParse(Arg(args,"--seconds"),out var duration)?Math.Clamp(duration,1,7200):1800;
                RunUsbTunnel(address,seconds,Arg(args,"--output"),Arg(args,"--ready"),Arg(args,"--stop-file")).GetAwaiter().GetResult();
            }
            catch(Exception){Environment.ExitCode=2;}
            return;
        }
        if (args.Contains("--vm-bridge"))
        {
            ApplicationConfiguration.Initialize();
            try { VmNativePolicy.RequireGuest(); Application.Run(new VmBridgeForm()); }
            catch (Exception) { MessageBox.Show("手机确认实验只支持当前 BandKey VMware Windows 11 build 26300 来宾；未修改系统登录配置。", "BandKey VM 实验"); Environment.ExitCode = 2; }
            return;
        }
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
    private static async Task RunUsbTunnel(System.Net.IPAddress address,int seconds,string? output,string? ready,string? stopFile)
    {
        await using var tunnel=new LoopbackTcpTunnel(19443,address,19443);tunnel.Start();
        if(ready!=null)File.WriteAllText(ready,"ready");
        for(int elapsed=0;elapsed<seconds && (stopFile==null || !File.Exists(stopFile));elapsed++)await Task.Delay(1000);
        if(output!=null)File.WriteAllText(output,JsonSerializer.Serialize(new{tunnel.Accepted,tunnel.Rejected,tunnel.Failed,loopbackOnly=true,tlsTerminated=false,windowsAuthenticationSubmitted=false},Store.Json));
    }
    private static async Task RunBleTunnel(string[] args,string? output)
    {
        var tunnel=new BlePhoneTransport();bool passed=false;string? error=null;
        using var rssi=args.Contains("--observe-peer-rssi")?new BlePeerRssiProbe(()=>tunnel.Peer):null;
        rssi?.Start();
        try{
            var target=System.Net.IPAddress.Parse(Arg(args,"--target")??throw new ArgumentException("Missing private target"));
            var seconds=int.TryParse(Arg(args,"--seconds"),out var value)?Math.Clamp(value,1,7200):1800;
            await tunnel.RunAsync(target,19443,TimeSpan.FromSeconds(seconds),Arg(args,"--ready"),Arg(args,"--stop-file"));passed=true;
        }catch(Exception failure){error=failure.GetType().Name;Environment.ExitCode=2;}
        if(output!=null)File.WriteAllText(output,JsonSerializer.Serialize(new{passed,error,tunnel.Stage,tunnel.Failure,tunnel.Attempts,tunnel.Failures,tunnel.FailureCount,tunnel.FailuresTruncated,tunnel.ConsecutiveFailures,tunnel.RecoveryBudgetResets,tunnel.Mtu,tunnel.DeliveryMode,tunnel.Streams,tunnel.SentBytes,tunnel.ReceivedBytes,peerRssi=rssi?.Snapshot,tlsTerminated=false,windowsAuthenticationSubmitted=false},Store.Json));
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
