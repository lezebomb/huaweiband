using BandKey.Core;

namespace BandKey.Windows;

public sealed class MainForm : Form
{
    private readonly Agent agent;
    private readonly ListBox devices = new() { Dock = DockStyle.Fill };
    private readonly ListBox authorized = new() { Width = 350, Height = 90 };
    private readonly Label state = new() { AutoSize = true, MaximumSize = new(950, 0), Padding = new(8) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(950, 0), Padding = new(8) };
    private readonly CheckBox autoLock = new() { Text = "启用离开后自动锁屏", AutoSize = true };
    private readonly CheckBox missingLock = new() { Text = "信号消失后也锁屏（先验证手环日常持续广播）", AutoSize = true };
    private readonly CheckBox bridge = new() { Text = "启用 HTTPS 手机桥接（保存后重启生效）", AutoSize = true };
    private readonly TextBox listen = new() { Width = 140 };
    private readonly NumericUpDown near = Number(-100, -10), far = Number(-120, -15), delay = Number(3, 600), missingDelay = Number(20, 1800);
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly bool startHidden;
    private bool ticking, exiting;
    private DeviceObservation[] displayed = [];
    public MainForm(Agent agent, bool startHidden, bool preview = false)
    {
        this.agent = agent; this.startHidden = startHidden;
        Text = "BandKey · 华为手环电脑钥匙"; Width = 1040; Height = 820; MinimumSize = new(850, 650);
        Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new(12) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        for (int i = 3; i < 7; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "BLE 广播仅用于距离观测；解锁按钮与 Windows 身份认证分别验证。当前版本尚不提供 Windows 解锁。", AutoSize = true, MaximumSize = new(950, 0) }, 0, 0);
        layout.Controls.Add(state, 0, 1); layout.Controls.Add(devices, 0, 2);
        var select = Flow();
        select.Controls.Add(Button("使用选中广播作为距离目标", (_, _) => SelectTarget()));
        select.Controls.Add(Button("清除目标", (_, _) => { agent.ApplySettings(agent.Settings with { BleAddress = null, BleAddressType = null, AutoLock = false }); autoLock.Checked = false; }));
        select.Controls.Add(Button("打开日志", (_, _) => OpenLog()));
        layout.Controls.Add(select, 0, 3);
        var settings = Flow();
        settings.Controls.Add(autoLock); settings.Controls.Add(missingLock);
        AddField(settings, "近阈值 dBm", near); AddField(settings, "远阈值 dBm", far); AddField(settings, "持续远离秒", delay); AddField(settings, "消失超时秒", missingDelay);
        settings.Controls.Add(bridge); AddField(settings, "本机监听 IP", listen);
        settings.Controls.Add(Button("保存设置", (_, _) => Save())); layout.Controls.Add(settings, 0, 4);
        var pair = Flow(); pair.Controls.Add(authorized);
        pair.Controls.Add(Button("导入手机公钥", (_, _) => ImportDevice()));
        pair.Controls.Add(Button("撤销选中手机", (_, _) => Revoke()));
        pair.Controls.Add(Button("导出电脑配对资料", (_, _) => ExportPairing())); layout.Controls.Add(pair, 0, 5);
        layout.Controls.Add(status, 0, 6); Controls.Add(layout);
        var s = agent.Settings; autoLock.Checked = s.AutoLock; missingLock.Checked = s.Presence.LockOnMissingSignal; bridge.Checked = s.BridgeEnabled;
        near.Value = s.Presence.NearRssi; far.Value = s.Presence.FarRssi; delay.Value = (decimal)s.Presence.FarSeconds; missingDelay.Value = (decimal)s.Presence.MissingSeconds; listen.Text = s.ListenAddress;
        var menu = new ContextMenuStrip(); menu.Items.Add("打开 BandKey", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("手机 BLE 通信实验",null,(_,_)=>{using var helper=new BleBridgeForm(new Store(Path.Combine(agent.Store.DirectoryPath,"ble-bridge")));helper.ShowDialog(this);});
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Text = "BandKey", Icon = SystemIcons.Shield, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => { Show(); Activate(); };
        timer.Tick += async (_, _) =>
        {
            if (ticking) return;
            ticking = true;
            try { await agent.TickAsync(); RefreshView(); }
            catch (Exception e) { agent.Store.Log("tick-error", e.GetType().Name); status.Text = "监控错误：" + e.GetType().Name; }
            finally { ticking = false; }
        };
        Shown += async (_, _) => { if (preview) { RefreshView(); return; } if (startHidden) Hide(); await agent.StartAsync(); RefreshAuthorized(); timer.Start(); };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += async (_, _) => { timer.Stop(); tray.Dispose(); if (!preview) await agent.DisposeAsync(); };
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); WindowsSession.WTSRegisterSessionNotification(Handle, 0); }
    protected override void OnHandleDestroyed(EventArgs e)
    { WindowsSession.WTSUnRegisterSessionNotification(Handle); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing)
    { if (disposing) { timer.Dispose(); tray.Dispose(); } base.Dispose(disposing); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WindowsSession.WmSessionChange)
        {
            if (m.WParam.ToInt32() == WindowsSession.SessionLock) agent.SetLocked(true);
            if (m.WParam.ToInt32() == WindowsSession.SessionUnlock) agent.SetLocked(false);
        }
        if (m.Msg == 0x218 && m.WParam.ToInt32() is 4 or 7 or 18) agent.ResetAfterResume();
        base.WndProc(ref m);
    }
    private void RefreshView()
    {
        var selected = devices.SelectedIndex >= 0 && devices.SelectedIndex < displayed.Length ? displayed[devices.SelectedIndex].Address : null;
        displayed = agent.Scanner.Devices.Where(d => (DateTimeOffset.UtcNow - d.SeenAt).TotalSeconds < 60).ToArray();
        devices.BeginUpdate(); devices.Items.Clear();
        foreach (var d in displayed) devices.Items.Add($"{d.Name,-24} {d.Address}  {d.Rssi,4} dBm  {d.AddressType} {d.AdvertisementType}  {(DateTimeOffset.UtcNow - d.SeenAt).TotalSeconds:0}s 前");
        devices.SelectedIndex = Array.FindIndex(displayed, d => d.Address == selected); devices.EndUpdate();
        var p = agent.Presence;
        state.Text = $"适配器：{agent.Adapter?.Name ?? "未知"} / {agent.Adapter?.RadioState ?? "未知"}　扫描：{(agent.Scanner.Healthy ? "运行" : "停止")}\n目标：{agent.Settings.BleAddress ?? "未选择"}　距离：{p.State}　滤波 RSSI：{p.FilteredRssi:0.0}　锁屏监控已就绪：{p.Armed}\nWindows 会话：{agent.Locked switch { true => "锁定", false => "已登录", _ => "未知" }}　登录后端：尚未实现";
        status.Text = agent.Status;
    }
    private void SelectTarget()
    {
        if (devices.SelectedIndex < 0 || devices.SelectedIndex >= displayed.Length) return;
        var d = displayed[devices.SelectedIndex];
        if (d.Anonymous || d.Rssi is < -120 or > -10) return;
        agent.ApplySettings(agent.Settings with { BleAddress = d.Address, BleAddressType = d.AddressType, AutoLock = false }); autoLock.Checked = false;
        status.Text = "已选择距离目标。请与运动健康中的地址/设备名核对；广播地址可轮换，不代表密码学授权。";
    }
    private void Save()
    {
        try
        {
            if (autoLock.Checked && agent.Settings.BleAddress == null) throw new ArgumentException("请先选择手环广播目标。");
            agent.ApplySettings(agent.Settings with { AutoLock = autoLock.Checked, BridgeEnabled = bridge.Checked, ListenAddress = listen.Text.Trim(), Presence = agent.Settings.Presence with { NearRssi = (int)near.Value, FarRssi = (int)far.Value, FarSeconds = (double)delay.Value, MissingSeconds = (double)missingDelay.Value, LockOnMissingSignal = missingLock.Checked } });
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "设置未保存"); }
    }
    private void ImportDevice()
    {
        using var dialog = new OpenFileDialog { Filter = "手机公钥 JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var device = System.Text.Json.JsonSerializer.Deserialize<PublicDevice>(File.ReadAllText(dialog.FileName), Store.Json) ?? throw new ArgumentException("Empty device");
            var fp = Protocol.Fingerprint(device.PublicKey);
            if (MessageBox.Show(this, $"请在手机上核对以下公钥指纹完全一致：\n{fp}\n\n授权 {device.Name}？", "核对手机身份", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            agent.Enroll(device); RefreshAuthorized();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "公钥导入失败"); }
    }
    private void RefreshAuthorized()
    { authorized.DataSource = agent.Broker.Devices; authorized.DisplayMember = nameof(PublicDevice.Name); }
    private void Revoke() { if (authorized.SelectedItem is PublicDevice d) { agent.Revoke(d.DeviceId); RefreshAuthorized(); } }
    private void ExportPairing()
    {
        using var dialog = new SaveFileDialog { Filter = "电脑配对资料|*.json", FileName = "bandkey-pc.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(agent.PairingBundle, Store.Json));
    }
    private void OpenLog()
    {
        agent.Store.Log("ui", "log-open");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { ArgumentList = { Path.Combine(agent.Store.DirectoryPath, "bandkey.log") }, UseShellExecute = false });
    }
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 75 };
    private static FlowLayoutPanel Flow() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new(2) };
    private static Button Button(string text, EventHandler action) { var b = new Button { Text = text, AutoSize = true, Margin = new(4) }; b.Click += action; return b; }
    private static void AddField(FlowLayoutPanel panel, string text, Control input) { panel.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 7, 0, 0) }); panel.Controls.Add(input); }
}
