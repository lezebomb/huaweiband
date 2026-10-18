using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BandKey.Core;

namespace BandKey.Windows;

public sealed class Agent : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ECDsa key;
    private readonly X509Certificate2 certificate;
    private readonly BridgeServer bridge = new();
    private readonly AutoLockController autoLock;
    private double retryAt, nextRadioProbe;
    private int failures;
    private bool? locked;
    public Store Store { get; }
    public AppSettings Settings { get; private set; }
    public BleScanner Scanner { get; } = new();
    public AuthorizationBroker Broker { get; }
    public string PcId { get; }
    public PresenceSnapshot Presence => autoLock.Presence;
    public string Status { get; private set; } = "启动中";
    public bool? Locked { get { lock (gate) return locked; } }
    public AdapterReport? Adapter { get; private set; }
    public Agent(Store store)
    {
        Store = store; Settings = store.Read<AppSettings>("settings.json") ?? new(); Settings.Validate();
        autoLock = new(Settings.Presence, new WindowsSessionController()) { Enabled = Settings.AutoLock };
        autoLock.LockAttempted += result =>
        {
            Status = result.Submitted ? "已向 Windows 请求锁屏，等待系统会话通知" : "Windows 锁屏请求失败：" + result.Code;
            Store.Log("lock-request", result.Code);
        };
        key = store.LoadSigningKey(); certificate = store.LoadTlsCertificate();
        PcId = Protocol.Fingerprint(Protocol.ExportPublicKey(key)); Broker = new(PcId, key);
        foreach (var device in store.Read<PublicDevice[]>("devices.json") ?? [])
        {
            if (device.TestOnly || device.ConfirmationSource == ConfirmationSources.TestClient) throw new ArgumentException("正式代理不加载模拟设备，请使用独立的 --test-flow。");
            Broker.Enroll(device);
        }
        Scanner.Observed += OnObserved;
        Scanner.Failed += code => { lock (gate) { autoLock.Reset("scanner-stopped"); RefreshContext(); Status = "蓝牙扫描停止：" + code; Store.Log("ble-stopped", code); } };
    }
    public async Task StartAsync()
    {
        Adapter = await BleScanner.ProbeAdapterAsync(); SetLocked(WindowsSession.IsLocked());
        try { Scanner.Start(); Status = "被动扫描中；请先选择手环并校准"; }
        catch (Exception e) { Status = "蓝牙不可用：" + e.GetType().Name; Store.Log("ble-start-failed", e.GetType().Name); }
        if (Settings.BridgeEnabled)
        {
            try { await bridge.StartAsync(Settings, certificate, Broker, OnApproval, authentication: new(Broker, new UnavailableAuthenticationBackend(), () => Locked)); }
            catch (Exception e) { Status = "桥接监听失败：" + e.GetType().Name; Store.Log("bridge-start-failed", e.GetType().Name); }
        }
    }
    private void OnApproval(ApprovalResult result)
    {
        lock (gate)
        {
            Status = result.Authorized ? "手环授权已验证；Windows 登录后端尚未可用，请使用 PIN/密码" : "授权拒绝：" + result.Code;
            Store.Log("authorization", result.Code);
        }
    }
    public void SetLocked(bool? value)
    {
        lock (gate)
        {
            if (locked != value)
            {
                // Resume / manual login must observe a fresh Near before another walk-away lock.
                Store.Log("session", value switch { true => "locked", false => "unlocked", _ => "unknown" });
            }
            autoLock.SetSession(value); locked = value; RefreshContext();
        }
    }
    public void ResetAfterResume()
    { lock (gate) { autoLock.SetSession(null); autoLock.Reset("power-transition"); Scanner.Dispose(); Broker.SetContext(false, false); locked = null; retryAt = 0; nextRadioProbe = 0; } }
    public void ApplySettings(AppSettings settings)
    {
        settings.Validate();
        lock (gate)
        {
            Store.Write("settings.json", settings); Settings = settings; autoLock.Reconfigure(settings.Presence); autoLock.Enabled = settings.AutoLock;
            RefreshContext(); Status = "设置已保存；桥接地址或开关变更需重启程序";
        }
    }
    public void Enroll(PublicDevice device)
    {
        if (device.TestOnly || device.ConfirmationSource == ConfirmationSources.TestClient) throw new ArgumentException("测试客户端只能在 --test-flow 隔离模拟器中使用。");
        lock (gate) { Broker.Enroll(device); Store.Write("devices.json", Broker.Devices); }
    }
    public void Revoke(string deviceId) { lock (gate) { Broker.Revoke(deviceId); Store.Write("devices.json", Broker.Devices); Status = "已撤销手机公钥授权"; } }
    public object PairingBundle => new
    {
        version = 1, pcId = PcId, pcPublicKey = Broker.PcPublicKey,
        endpoint = $"https://{(Settings.ListenAddress.Contains(':') ? "[" + Settings.ListenAddress + "]" : Settings.ListenAddress)}:{Settings.Port}",
        certificateSha256 = BridgeServer.CertificatePin(certificate)
    };
    private void OnObserved(DeviceObservation sample)
    {
        lock (gate)
        {
            if (Settings.BleAddress == null || !new ObservationTarget(Settings.BleAddress, Settings.BleAddressType).Matches(sample)) return;
            var age = (DateTimeOffset.UtcNow - sample.SeenAt).TotalSeconds;
            if (age < -5 || age >= Settings.Presence.StaleSeconds) return;
            autoLock.Observe(sample.Rssi, clock.Elapsed.TotalSeconds, Scanner.Healthy && Adapter?.RadioState == "On");
            RefreshContext();
        }
    }
    private void RefreshContext() => Broker.SetContext(locked == true, Scanner.Healthy && Adapter?.RadioState == "On" && Presence.State == PresenceState.Near);
    public async Task TickAsync()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now >= nextRadioProbe)
        {
            nextRadioProbe = now + 5;
            Adapter = await BleScanner.ProbeAdapterAsync();
            SetLocked(WindowsSession.IsLocked());
        }
        lock (gate)
        {
            if (!Scanner.Healthy && now >= retryAt)
            {
                retryAt = now + Math.Min(60, Math.Pow(2, Math.Min(++failures, 6)));
                try { Scanner.Start(); Store.Log("ble-restart", "attempt"); }
                catch (Exception e) { Store.Log("ble-restart-failed", e.GetType().Name); }
            }
            if (Scanner.Healthy) failures = 0;
            autoLock.Tick(clock.Elapsed.TotalSeconds, Scanner.Healthy && Adapter?.RadioState == "On");
            RefreshContext();
        }
    }
    public async ValueTask DisposeAsync()
    { Scanner.Dispose(); await bridge.DisposeAsync(); certificate.Dispose(); key.Dispose(); }
}
