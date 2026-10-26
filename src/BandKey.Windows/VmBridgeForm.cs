using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using BandKey.Core;

namespace BandKey.Windows;

internal sealed class VmBridgeForm : Form
{
    private readonly TextBox bind = new() { Text = "127.0.0.1", Width = 240 }, endpoint = new() { Text = "https://127.0.0.1:19443", Width = 440 };
    private readonly TextBox status = new() { Multiline = true, ReadOnly = true, Width = 660, Height = 230, ScrollBars = ScrollBars.Vertical };
    private readonly Button start = new() { Text = "启动 VM 手机中继", AutoSize = true }, stop = new() { Text = "停止并撤销本轮请求", AutoSize = true };
    private byte[]? nativePublicKey;
    private BridgeServer? server;
    private WindowsGrantRelay? relay;
    private ECDsa? pc;
    private X509Certificate2? certificate;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private string? pairing;
    private bool stopping;
    private readonly CheckBox fixedMode = new() { Text = "手机固定在电脑旁：用手环连接状态判断返回", AutoSize = true };
    private readonly CheckBox walkLock = new() { Text = "手环持续断连后自动锁定此 VM（默认关闭）", AutoSize = true, Enabled = false };
    private readonly NumericUpDown nearDelay=new(){Minimum=1,Maximum=10,Value=1,Width=80};
    private FixedPhoneChannel? fixedPhone;
    private FixedPhoneSnapshot? lastFixedPhone;
    private readonly SessionEvidence sessionEvidence = new(false);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;
    private readonly Label sessionStatus = new() { AutoSize = true, MaximumSize = new(660, 0) };
    private readonly string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BandKeyVmBridge", "reports", "relay-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
    private WindowsGrantRelayActivity? lastActivity;
    private bool notificationsRegistered;
    private int registrationError, powerResumes;
    private string? reportError;
    private double lastSaved;
    public VmBridgeForm()
    {
        VmNativePolicy.RequireGuest(); Text = "BandKey VM 手机通信实验"; ClientSize = new(720, 610); StartPosition = FormStartPosition.CenterScreen;
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true }; Controls.Add(column);
        column.Controls.Add(new Label { Text = "仅供隔离 VM：手机／手环确认，Windows 原生组件独立验签。\n固定手机模式使用手环连接范围，不提供米数距离。", AutoSize = true });
        var import = new Button { Text = "1. 导入手机实验公钥（.bandkeypub）", AutoSize = true }; import.Click += (_, _) => ImportPhone(); column.Controls.Add(import);
        column.Controls.Add(new Label { Text = "本机监听 IPv4（端口固定 19443）", AutoSize = true }); column.Controls.Add(bind);
        column.Controls.Add(new Label { Text = "手机访问地址（NAT 映射时填写宿主机地址）", AutoSize = true }); column.Controls.Add(endpoint);
        fixedMode.CheckedChanged += (_, _) => { walkLock.Enabled = fixedMode.Checked; if (!fixedMode.Checked) walkLock.Checked = false; };
        column.Controls.Add(fixedMode); column.Controls.Add(walkLock);
        column.Controls.Add(new Label{Text="固定手机：接近稳定等待（秒，默认1；远离仍15秒）",AutoSize=true});column.Controls.Add(nearDelay);
        start.Click += async (_, _) => await StartRelay(); column.Controls.Add(start);
        var retryRequest = new Button { Text = "重新请求手环确认（固定手机模式）", AutoSize = true };
        retryRequest.Click += (_, _) => fixedPhone?.RetryRequest(); column.Controls.Add(retryRequest);
        var export = new Button { Text = "2. 导出电脑配对资料给手机", AutoSize = true }; export.Click += (_, _) => ExportPairing(); column.Controls.Add(export);
        stop.Click += async (_, _) => await StopRelay(); column.Controls.Add(stop); column.Controls.Add(status);
        column.Controls.Add(sessionStatus);
        var exportReport = new Button { Text = "导出本次会话记录（不含配对资料）", AutoSize = true };
        exportReport.Click += (_, _) => ExportReport(); column.Controls.Add(exportReport);
        status.Text = "先用安装器的手机确认模式登记同一公钥，再启动中继。\r\n默认只监听本机；手机连通需配置 VM 网络及专用网络防火墙。";
        var enrolled=VmNativePolicy.ReadEnrolledPhone();
        if(enrolled!=null){nativePublicKey=enrolled;status.Text="已读取本VM当前账户登记的手机公钥，无需重新导入。\r\nSHA-256：\r\n"+Convert.ToHexStringLower(SHA256.HashData(enrolled));}
        try{
        var localAddresses=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address)
            .Where(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Where(a=>{var b=a.GetAddressBytes();return b[0]==10 || b[0]==192 && b[1]==168 || b[0]==172 && b[1]>=16 && b[1]<=31;}).Distinct().ToArray();
        if(localAddresses.Length==1)bind.Text=localAddresses[0].ToString();
        }catch(System.Net.NetworkInformation.NetworkInformationException){/* Keep manual address entry available. */}
        timer.Tick += async (_, _) => { if (relay != null && nativePublicKey != null && !VmNativePolicy.MatchesPhone(nativePublicKey)) { await StopRelay(); status.AppendText("\r\n原生登记已撤销或改变，中继已停止。"); } ObserveSession(); };
        Shown += (_, _) => { ObserveSession(); timer.Start(); };
        FormClosing += async (_, args) => { if (stopping) { args.Cancel = true; return; } if (server != null) { args.Cancel = true; await StopRelay(); Close(); } }; FormClosed += (_, _) => { timer.Stop(); SaveReport(); timer.Dispose(); };
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        notificationsRegistered = WindowsSession.WTSRegisterSessionNotification(Handle, 0);
        if (!notificationsRegistered) registrationError = Marshal.GetLastWin32Error();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (notificationsRegistered) { WindowsSession.WTSUnRegisterSessionNotification(Handle); notificationsRegistered = false; }
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowsSession.WmSessionChange && message.LParam.ToInt64() == sessionId && message.WParam.ToInt64() is WindowsSession.SessionLock or WindowsSession.SessionUnlock)
        {
            sessionEvidence.Observe(message.WParam.ToInt64() == WindowsSession.SessionLock, ObservationSource.Notification, clock.Elapsed.TotalMilliseconds);
            SaveReport(); RefreshSessionStatus();
        }
        if (message.Msg == 0x218 && message.WParam.ToInt64() is 7 or 0x12)
        {
            powerResumes++; relay?.Revoke();
            // A fresh relay is required after resume; no later decision can use this relay.
            _ = StopAfterResume();
        }
        base.WndProc(ref message);
    }
    private async Task StopAfterResume()
    {
        await StopRelay();
        if (!IsDisposed) status.AppendText("\r\n系统恢复：本轮中继已撤销，请重新启动中继。");
    }
    private void ObserveSession()
    {
        sessionEvidence.Observe(WindowsSession.IsLocked(), ObservationSource.Query, clock.Elapsed.TotalMilliseconds);
        if (clock.Elapsed.TotalMilliseconds - lastSaved >= 5000) SaveReport();
        RefreshSessionStatus();
    }
    private void RefreshSessionStatus()
    {
        var activity = relay?.Activity ?? lastActivity;
        sessionStatus.Text = $"会话记录：WTS {(notificationsRegistered ? "已注册" : "不可用，保留查询结果")}；观察到锁定后解锁：通知 {sessionEvidence.NotificationUnlocksAfterLock}／查询 {sessionEvidence.QueryUnlocksAfterLock}\n最近一轮中继：授权文件交付 {activity?.ApprovalFilesSubmitted ?? 0}；签名拒绝 {activity?.SignedDenials ?? 0}；无效／失败 {activity?.Rejections ?? 0}\n交付与会话解锁分别记录，不推断解锁由 BandKey 导致。\n{(reportError == null ? reportPath : "报告保存失败：" + reportError)}";
        var range = fixedPhone?.Snapshot ?? lastFixedPhone;
        if (range != null) sessionStatus.Text += $"\n固定手机：手环连接 {range.BandConnected?.ToString() ?? "Unknown"}；接近状态 {range.Presence}；有效报告 {range.Reports}；新请求 {range.Renewals}；原生就绪 {range.NativeRequestReady}；{range.NativeState}";
    }
    private void SaveReport()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var report = new { schema = "bandkey.vm-relay-observation.v1", generatedAt = DateTimeOffset.UtcNow,
                session = sessionEvidence.Report(), latestRelayActivity = relay?.Activity ?? lastActivity, relayRunning = relay != null,
                fixedPhone = fixedPhone?.Snapshot ?? lastFixedPhone,
                notificationsRegistered, registrationError, powerResumes,
                unlockAttributedToBandKey = false, credentialsAccessedByObserver = false };
            File.WriteAllText(reportPath + ".tmp", JsonSerializer.Serialize(report, Store.Json)); File.Move(reportPath + ".tmp", reportPath, true);
            reportError = null; lastSaved = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { reportError = error.GetType().Name; }
    }
    private void ExportReport()
    {
        SaveReport(); if (reportError != null) { MessageBox.Show(this, "报告未保存：" + reportError); return; }
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "bandkey-vm-session-report.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.Copy(reportPath, dialog.FileName, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "导出失败：" + error.GetType().Name); }
    }
    private void ImportPhone()
    {
        if (server != null) { MessageBox.Show(this, "先停止当前中继。"); return; }
        using var dialog = new OpenFileDialog { Filter = "BandKey 公钥|*.bandkeypub", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        nativePublicKey = null;
        try
        {
            if (new FileInfo(dialog.FileName).Length != 72) throw new FormatException();
            var bytes = File.ReadAllBytes(dialog.FileName); using var key = WindowsGrantWire.ImportNativePublicKey(bytes); nativePublicKey = bytes;
            status.Text = "手机实验公钥 SHA-256（与手机核对）：\r\n" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (Exception) { MessageBox.Show(this, "无法读取有效的 P-256 手机实验公钥。"); }
    }
    private async Task StartRelay()
    {
        if (server != null || stopping) return;
        try
        {
            if (nativePublicKey == null || !VmNativePolicy.MatchesPhone(nativePublicKey)) throw new InvalidOperationException("先在 VM 安装器选择手机确认模式，登记相同公钥。");
            if (fixedMode.Checked && !VmNativePolicy.SupportsRequestRenewal()) throw new InvalidOperationException("固定手机模式需要本轮协议 v3 的 VM 登录组件。请按新教程在快照保护下更新 VM；主机不要安装。");
            if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" || !IPAddress.TryParse(url.Host, out _) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/") throw new InvalidOperationException("手机地址必须为 HTTPS IPv4/IPv6 地址和端口。");
            var settings = new AppSettings { ListenAddress = bind.Text.Trim(), Port = 19443 }; settings.Validate();
            var store = new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BandKeyVmBridge"));
            pc = store.LoadSigningKey(); certificate = store.LoadTlsCertificate(); using var phone = WindowsGrantWire.ImportNativePublicKey(nativePublicKey);
            var publicKey = Protocol.ExportPublicKey(phone); var device = new PublicDevice(Protocol.Fingerprint(publicKey), "VM phone test", publicKey, ConfirmationSources.TestClient, true);
            bool Current() => nativePublicKey != null && VmNativePolicy.MatchesPhone(nativePublicKey);
            using var identity = WindowsIdentity.GetCurrent();
            var raw = new WindowsGrantDirectoryExchange(VmNativePolicy.DirectoryPath, identity.User!.Value, (uint)Process.GetCurrentProcess().SessionId, () => Current() && WindowsSession.IsLocked() == true);
            if (fixedMode.Checked) fixedPhone = new FixedPhoneChannel(pc, device, raw, new VmGuardedSession(), Current, walkLock.Checked,options:new PresenceOptions{NearSeconds=(double)nearDelay.Value},useFreshLockRequest:true);
            var exchange = new WindowsGrantDirectoryExchange(VmNativePolicy.DirectoryPath, identity.User!.Value, (uint)Process.GetCurrentProcess().SessionId, () => Current() && WindowsSession.IsLocked() == true && (fixedPhone?.CanSubmit ?? true));
            relay = new WindowsGrantRelay(pc, device, exchange, Current, proximityCurrent: () => fixedPhone?.CanSubmit ?? true);
            server = new BridgeServer(); var emptyLegacy = new AuthorizationBroker(relay.PcId, pc);
            await server.StartAsync(settings, certificate, emptyLegacy, _ => { }, windowsRelay: relay, fixedPhone: fixedPhone);
            pairing = System.Text.Json.JsonSerializer.Serialize(new { endpoint = url.GetLeftPart(UriPartial.Authority), pcId = relay.PcId, pcPublicKey = Protocol.ExportPublicKey(pc), certificateSha256 = BridgeServer.CertificatePin(certificate), protocol = "bandkey-vm-phone-v1", testOnly = true }, Store.Json);
            bind.Enabled = endpoint.Enabled = fixedMode.Enabled = walkLock.Enabled = nearDelay.Enabled = false; start.Enabled = false;
            status.AppendText("\r\n中继已启动（0.9 新锁屏请求直达，重建恢复）。电脑 ID：\r\n" + relay.PcId + "\r\nTLS：\r\n" + BridgeServer.CertificatePin(certificate) + "\r\n手机访问：" + url.GetLeftPart(UriPartial.Authority) + "\r\n导出配对资料，在手机实验页导入并核对指纹。");
        }
        catch (Exception error) { await StopRelay(); MessageBox.Show(this, error is InvalidOperationException ? error.Message : "中继启动失败：" + error.GetType().Name); }
    }
    private void ExportPairing()
    {
        if (pairing == null) { MessageBox.Show(this, "先成功启动中继。"); return; }
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "bandkey-vm-pc.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, pairing);
    }
    private async Task StopRelay()
    {
        if (stopping) return;
        stopping = true;
        relay?.Revoke(); lastActivity = relay?.Activity ?? lastActivity; relay = null; var active = server; server = null;
        fixedPhone?.Revoke(); lastFixedPhone = fixedPhone?.Snapshot ?? lastFixedPhone; fixedPhone = null;
        try { if (active != null) await active.DisposeAsync(); }
        catch (Exception error) { status.AppendText("\r\n监听器停止异常：" + error.GetType().Name + "；本轮授权已撤销。"); }
        finally
        {
            pc?.Dispose(); pc = null; certificate?.Dispose(); certificate = null; pairing = null;
            bind.Enabled = endpoint.Enabled = start.Enabled = true; stopping = false;
            fixedMode.Enabled = nearDelay.Enabled = true; walkLock.Enabled = fixedMode.Checked;
            SaveReport(); if (!IsDisposed) RefreshSessionStatus();
        }
    }
}
