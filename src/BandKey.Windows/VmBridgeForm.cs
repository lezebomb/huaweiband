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
    private readonly NativePhonePolicy nativePolicy;
    private readonly bool hostPilot;
    private readonly NotifyIcon? residentIcon;
    private CancellationTokenSource? residentStop;
    private Task? residentTransport;
    private bool hostPaused,exitRequested,tls12Compatibility;
    private long hostStarted,hostRetryAt;
    private string residentStatus="等待连接";
    private BlePhoneTransport? activeResidentTransport;
    private object? lastResidentTransportReport;
    private object? ResidentTransportReport()=>activeResidentTransport is {} t?new{stage=t.Stage,attempts=t.Attempts,streams=t.Streams,abortedStreams=t.AbortedStreams,mtu=t.Mtu,failures=t.Failures,sentBytes=t.SentBytes,receivedBytes=t.ReceivedBytes}:lastResidentTransportReport;
    private sealed record HostSettings(bool Paused=false,bool AutoLock=false,bool Tls12Compatibility=false);
    private string HostData=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BandKeyHostBridge");
    private void SaveHostSettings(){if(hostPilot)try{new Store(HostData).Write("resident-settings.json",new HostSettings(hostPaused,walkLock.Checked,tls12Compatibility));}catch(Exception error)when(error is IOException or UnauthorizedAccessException){reportError="settings-write-failed";}}
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
    private readonly string reportPath;
    private WindowsGrantRelayActivity? lastActivity;
    private bool notificationsRegistered;
    private int registrationError, powerResumes;
    private string? reportError;
    private double lastSaved;
    public VmBridgeForm(bool hostPilot=false,bool resident=false)
    {
        this.hostPilot=hostPilot;nativePolicy=new(hostPilot);nativePolicy.RequireEnvironment();
        reportPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),hostPilot?"BandKeyHostBridge":"BandKeyVmBridge","reports","relay-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");
        Text = hostPilot?"BandKey 主机试用":"BandKey VM 手机通信实验"; ClientSize = new(720, 610); StartPosition = FormStartPosition.CenterScreen;
        if(hostPilot){
            start.Text="启用／恢复主机手环解锁";fixedMode.Text="手机签名状态：手环在线、通信恢复后请求确认";walkLock.Text="手环持续断连后锁定本机（默认关闭）";bind.ReadOnly=true;endpoint.ReadOnly=true;fixedMode.Checked=true;
            try{var saved=new Store(HostData).Read<HostSettings>("resident-settings.json");hostPaused=saved?.Paused??false;walkLock.Checked=saved?.AutoLock??false;tls12Compatibility=saved?.Tls12Compatibility??false;}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){hostPaused=true;reportError="settings-unreadable";}
            walkLock.CheckedChanged+=(_,_)=>SaveHostSettings();
            residentIcon=new NotifyIcon{Icon=SystemIcons.Shield,Text="BandKey 主机常驻",Visible=true};
            var menu=new ContextMenuStrip();menu.Items.Add("显示状态",null,(_,_)=>{Show();WindowState=FormWindowState.Normal;Activate();});
            menu.Items.Add("暂停",null,async(_,_)=>{hostPaused=true;SaveHostSettings();await StopRelay();});
            menu.Items.Add("恢复",null,async(_,_)=>{hostPaused=false;SaveHostSettings();await StartRelay();});
            menu.Items.Add("退出（下次登录自动启动）",null,(_,_)=>{exitRequested=true;Close();});residentIcon.ContextMenuStrip=menu;residentIcon.DoubleClick+=(_,_)=>{Show();Activate();};
        }
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true }; Controls.Add(column);
        column.Controls.Add(new Label { Text = hostPilot?"主机常驻：手动锁屏，返回后仍需手环点确认。\n手机可留在桌旁，也可随身；通信恢复后使用新请求，不提供米数距离。":"仅供隔离 VM：手机／手环确认，Windows 原生组件独立验签。\n固定手机模式使用手环连接范围，不提供米数距离。", AutoSize = true });
        var import = new Button { Text = "1. 导入手机实验公钥（.bandkeypub）", AutoSize = true }; import.Click += (_, _) => ImportPhone(); column.Controls.Add(import);
        column.Controls.Add(new Label { Text = "本机监听 IPv4（端口固定 19443）", AutoSize = true }); column.Controls.Add(bind);
        column.Controls.Add(new Label { Text = "手机访问地址（NAT 映射时填写宿主机地址）", AutoSize = true }); column.Controls.Add(endpoint);
        fixedMode.CheckedChanged += (_, _) => { walkLock.Enabled = fixedMode.Checked; if (!fixedMode.Checked) walkLock.Checked = false; };
        column.Controls.Add(fixedMode); column.Controls.Add(walkLock);
        column.Controls.Add(new Label{Text=hostPilot?"返回稳定等待（秒，默认1）":"固定手机：接近稳定等待（秒，默认1；断连15秒锁屏）",AutoSize=true});column.Controls.Add(nearDelay);
        start.Click += async (_, _) => {hostPaused=false;SaveHostSettings();await StartRelay();}; column.Controls.Add(start);
        var retryRequest = new Button { Text = "重新请求手环确认（固定手机模式）", AutoSize = true };
        retryRequest.Click += (_, _) => fixedPhone?.RetryRequest(); column.Controls.Add(retryRequest);
        var export = new Button { Text = "2. 导出电脑配对资料给手机", AutoSize = true }; export.Click += (_, _) => ExportPairing(); column.Controls.Add(export);
        stop.Click += async (_, _) => {if(hostPilot){hostPaused=true;SaveHostSettings();}await StopRelay();}; column.Controls.Add(stop); column.Controls.Add(status);
        column.Controls.Add(sessionStatus);
        var exportReport = new Button { Text = "导出本次会话记录（不含配对资料）", AutoSize = true };
        exportReport.Click += (_, _) => ExportReport(); column.Controls.Add(exportReport);
        status.Text = "先用安装器的手机确认模式登记同一公钥，再启动中继。\r\n默认只监听本机；手机连通需配置 VM 网络及专用网络防火墙。";
        var enrolled=nativePolicy.ReadEnrolledPhone();
        if(enrolled!=null){nativePublicKey=enrolled;status.Text="已读取本VM当前账户登记的手机公钥，无需重新导入。\r\nSHA-256：\r\n"+Convert.ToHexStringLower(SHA256.HashData(enrolled));}
        try{
        var localAddresses=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address)
            .Where(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Where(a=>{var b=a.GetAddressBytes();return b[0]==10 || b[0]==192 && b[1]==168 || b[0]==172 && b[1]>=16 && b[1]<=31;}).Distinct().ToArray();
        if(!hostPilot && localAddresses.Length==1)bind.Text=localAddresses[0].ToString();
        }catch(System.Net.NetworkInformation.NetworkInformationException){/* Keep manual address entry available. */}
        timer.Tick += async (_, _) => {
            if(hostPilot && File.Exists(Path.Combine(HostData,"stop-resident.request"))){hostPaused=true;SaveHostSettings();exitRequested=true;await StopRelay();Close();return;}
            if (relay != null && nativePublicKey != null && !nativePolicy.MatchesPhone(nativePublicKey)) { await StopRelay(); status.AppendText("\r\n原生登记已撤销或改变，中继已停止。"); }
            if(hostPilot && !hostPaused && !stopping){
                if(server!=null && Environment.TickCount64-hostStarted>=25*60*1000)await StopRelay();
                if(server==null && Environment.TickCount64>=hostRetryAt){hostRetryAt=Environment.TickCount64+30000;nativePublicKey=nativePolicy.ReadEnrolledPhone();if(nativePublicKey!=null)await StartRelay();}
            }
            ObserveSession();
        };
        Shown += async (_, _) => {if(hostPilot)File.Delete(Path.Combine(HostData,"stop-resident.request"));ObserveSession();timer.Start();if(hostPilot && !hostPaused){await StartRelay();if(resident && server!=null)Hide();} };
        FormClosing += async (_, args) => {if(hostPilot && !exitRequested){args.Cancel=true;Hide();return;}if(stopping){args.Cancel=true;return;}if(server!=null){args.Cancel=true;await StopRelay();Close();}};
        FormClosed += (_, _) => { timer.Stop();SaveReport();timer.Dispose();residentIcon?.Dispose(); };
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
        if (!IsDisposed) status.AppendText(hostPilot?"\r\n系统恢复：旧请求已撤销，正在自动重新连接。":"\r\n系统恢复：本轮中继已撤销，请重新启动中继。");
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
        if(hostPilot)sessionStatus.Text+="\n常驻传输："+(activeResidentTransport?.Stage??residentStatus)+(hostPaused?"（已暂停）":"");
    }
    private void SaveReport()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var report = new { schema = "bandkey.vm-relay-observation.v1", generatedAt = DateTimeOffset.UtcNow,
                session = sessionEvidence.Report(), latestRelayActivity = relay?.Activity ?? lastActivity, relayRunning = relay != null,
                fixedPhone = fixedPhone?.Snapshot ?? lastFixedPhone,
                notificationsRegistered, registrationError, powerResumes, profile=hostPilot?"host-pilot":"vm-lab",residentTransport=ResidentTransportReport(),tls12Compatibility,departureSeconds=hostPilot?5:15,
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
        try
        {
            if (new FileInfo(dialog.FileName).Length != 72) throw new FormatException();
            var bytes = File.ReadAllBytes(dialog.FileName); using var key = WindowsGrantWire.ImportNativePublicKey(bytes);
            var enrolled = nativePolicy.ReadEnrolledPhone();
            if (enrolled != null && !bytes.AsSpan().SequenceEqual(enrolled))
            {
                nativePublicKey = enrolled;
                status.Text = "导入文件与本VM当前账户登记不一致，已保留登记公钥。\r\nSHA-256：\r\n" + Convert.ToHexStringLower(SHA256.HashData(enrolled));
                MessageBox.Show(this, "这份文件属于另一份手机公钥。已保留本VM当前账户登记，继续使用它即可，无需重新安装。\n若要更换手机，请先按VM安装教程变更登记。");
                return;
            }
            nativePublicKey = bytes;
            status.Text = "手机实验公钥 SHA-256（与手机核对）：\r\n" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (Exception) { MessageBox.Show(this, "无法读取有效的 P-256 手机实验公钥，原来已读取的公钥保持不变。"); }
    }
    private async Task StartRelay()
    {
        if (server != null || stopping) return;
        try
        {
            if (nativePublicKey == null || !nativePolicy.MatchesPhone(nativePublicKey)) throw new InvalidOperationException("没有匹配当前VM账户的有效手机登记。请先关闭并重新打开中继，让程序自动读取登记公钥；仍不符时核对VM登录账户和手机公钥，不要直接重复安装。");
            if (fixedMode.Checked && !nativePolicy.SupportsRequestRenewal()) throw new InvalidOperationException("固定手机模式需要本轮协议 v3 的 VM 登录组件。请按新教程在快照保护下更新 VM；主机不要安装。");
            if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" || !IPAddress.TryParse(url.Host, out _) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/") throw new InvalidOperationException("手机地址必须为 HTTPS IPv4/IPv6 地址和端口。");
            var settings = new AppSettings { ListenAddress = bind.Text.Trim(), Port = 19443 }; settings.Validate();
            var store = new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), hostPilot?"BandKeyHostBridge":"BandKeyVmBridge"));
            pc = store.LoadSigningKey(); certificate = store.LoadTlsCertificate(); using var phone = WindowsGrantWire.ImportNativePublicKey(nativePublicKey);
            var publicKey = Protocol.ExportPublicKey(phone); var device = new PublicDevice(Protocol.Fingerprint(publicKey), "VM phone test", publicKey, ConfirmationSources.TestClient, true);
            bool Current() => nativePublicKey != null && nativePolicy.MatchesPhone(nativePublicKey);
            using var identity = WindowsIdentity.GetCurrent();
            var raw = new WindowsGrantDirectoryExchange(nativePolicy.DirectoryPath, identity.User!.Value, (uint)Process.GetCurrentProcess().SessionId, () => Current() && WindowsSession.IsLocked() == true);
            if (fixedMode.Checked) fixedPhone = new FixedPhoneChannel(pc, device, raw, new NativeGuardedSession(nativePolicy), Current, walkLock.Checked,options:new PresenceOptions{NearSeconds=(double)nearDelay.Value,FarSeconds=hostPilot?5:15},useFreshLockRequest:true,renewOnAuthenticatedReturn:hostPilot);
            var exchange = new WindowsGrantDirectoryExchange(nativePolicy.DirectoryPath, identity.User!.Value, (uint)Process.GetCurrentProcess().SessionId, () => Current() && WindowsSession.IsLocked() == true && (fixedPhone?.CanSubmit ?? true));
            relay = new WindowsGrantRelay(pc, device, exchange, Current, proximityCurrent: () => fixedPhone?.CanSubmit ?? true);
            server = new BridgeServer(); var emptyLegacy = new AuthorizationBroker(relay.PcId, pc);
            await server.StartAsync(settings, certificate, emptyLegacy, _ => { }, windowsRelay: relay, fixedPhone: fixedPhone,tls12Compatibility:hostPilot && tls12Compatibility);
            pairing = System.Text.Json.JsonSerializer.Serialize(new { endpoint = url.GetLeftPart(UriPartial.Authority), pcId = relay.PcId, pcPublicKey = Protocol.ExportPublicKey(pc), certificateSha256 = BridgeServer.CertificatePin(certificate), protocol = hostPilot?"bandkey-host-pilot-v1":"bandkey-vm-phone-v1", testOnly = true, realWearableOnly=hostPilot }, Store.Json);
            if(hostPilot){Directory.CreateDirectory(HostData);File.WriteAllText(Path.Combine(HostData,"public-pairing.json"),pairing);hostStarted=Environment.TickCount64;residentStop=new();residentTransport=RunResidentTransport(residentStop.Token);}
            bind.Enabled = endpoint.Enabled = fixedMode.Enabled = walkLock.Enabled = nearDelay.Enabled = false; start.Enabled = false;
            status.AppendText("\r\n中继已启动（0.9 新锁屏请求直达，重建恢复）。电脑 ID：\r\n" + relay.PcId + "\r\nTLS：\r\n" + BridgeServer.CertificatePin(certificate) + "\r\n手机访问：" + url.GetLeftPart(UriPartial.Authority) + "\r\n导出配对资料，在手机实验页导入并核对指纹。");
        }
        catch (Exception error) { await StopRelay();if(hostPilot)status.AppendText("\r\n启动等待："+error.GetType().Name);else MessageBox.Show(this,error is InvalidOperationException?error.Message:"中继启动失败："+error.GetType().Name); }
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
        relay?.Revoke();fixedPhone?.Revoke();
        residentStop?.Cancel();if(residentTransport!=null){try{await residentTransport;}catch(OperationCanceledException){}residentTransport=null;}residentStop?.Dispose();residentStop=null;
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
    private async Task RunResidentTransport(CancellationToken token){
        while(!token.IsCancellationRequested){
            BlePhoneTransport? transport=null;
            transport=new BlePhoneTransport(()=>new BlePhoneTunnel(drainTargetAbort:true),(burst,stop)=>Task.Delay(TimeSpan.FromSeconds(transport?.Failure?.Code=="connect-scan"?2:10+(1<<(burst-1))),stop));
            activeResidentTransport=transport;residentStatus="正在连接手机 BLE";
            try{await transport.RunAsync(IPAddress.Loopback,19443,TimeSpan.FromHours(2),cancellation:token);residentStatus="传输已结束，准备新连接";}
            catch(OperationCanceledException)when(token.IsCancellationRequested){return;}
            catch(Exception error){residentStatus="等待重连："+(transport.Failure?.Code??error.GetType().Name);}
            finally{lastResidentTransportReport=ResidentTransportReport();activeResidentTransport=null;}
            if(!token.IsCancellationRequested)try{await Task.Delay(TimeSpan.FromSeconds(transport.Failure?.Code=="connect-scan"?2:30),token);}catch(OperationCanceledException){return;}
        }
    }
}
