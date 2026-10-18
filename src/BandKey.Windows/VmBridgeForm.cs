using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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
    public VmBridgeForm()
    {
        VmNativePolicy.RequireGuest(); Text = "BandKey VM 手机通信实验"; ClientSize = new(720, 610); StartPosition = FormStartPosition.CenterScreen;
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true }; Controls.Add(column);
        column.Controls.Add(new Label { Text = "仅供隔离 VM：手机测试按钮确认，Windows 原生组件独立验签。\n本程序不读取或传输 VM 密码；当前不验证距离或真实手环。", AutoSize = true });
        var import = new Button { Text = "1. 导入手机实验公钥（.bandkeypub）", AutoSize = true }; import.Click += (_, _) => ImportPhone(); column.Controls.Add(import);
        column.Controls.Add(new Label { Text = "本机监听 IPv4（端口固定 19443）", AutoSize = true }); column.Controls.Add(bind);
        column.Controls.Add(new Label { Text = "手机访问地址（NAT 映射时填写宿主机地址）", AutoSize = true }); column.Controls.Add(endpoint);
        start.Click += async (_, _) => await StartRelay(); column.Controls.Add(start);
        var export = new Button { Text = "2. 导出电脑配对资料给手机", AutoSize = true }; export.Click += (_, _) => ExportPairing(); column.Controls.Add(export);
        stop.Click += async (_, _) => await StopRelay(); column.Controls.Add(stop); column.Controls.Add(status);
        status.Text = "先用安装器的手机确认模式登记同一公钥，再启动中继。\r\n默认只监听本机；手机连通需配置 VM 网络及专用网络防火墙。";
        timer.Tick += async (_, _) => { if (relay != null && nativePublicKey != null && !VmNativePolicy.MatchesPhone(nativePublicKey)) { await StopRelay(); status.AppendText("\r\n原生登记已撤销或改变，中继已停止。"); } }; timer.Start();
        FormClosing += async (_, args) => { if (stopping) { args.Cancel = true; return; } if (server != null) { args.Cancel = true; await StopRelay(); Close(); } }; FormClosed += (_, _) => timer.Dispose();
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
            if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" || !IPAddress.TryParse(url.Host, out _) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/") throw new InvalidOperationException("手机地址必须为 HTTPS IPv4/IPv6 地址和端口。");
            var settings = new AppSettings { ListenAddress = bind.Text.Trim(), Port = 19443 }; settings.Validate();
            var store = new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BandKeyVmBridge"));
            pc = store.LoadSigningKey(); certificate = store.LoadTlsCertificate(); using var phone = WindowsGrantWire.ImportNativePublicKey(nativePublicKey);
            var publicKey = Protocol.ExportPublicKey(phone); var device = new PublicDevice(Protocol.Fingerprint(publicKey), "VM phone test", publicKey, ConfirmationSources.TestClient, true);
            bool Current() => nativePublicKey != null && VmNativePolicy.MatchesPhone(nativePublicKey);
            using var identity = WindowsIdentity.GetCurrent();
            var exchange = new WindowsGrantDirectoryExchange(VmNativePolicy.DirectoryPath, identity.User!.Value, (uint)Process.GetCurrentProcess().SessionId, () => Current() && WindowsSession.IsLocked() == true);
            relay = new WindowsGrantRelay(pc, device, exchange, Current);
            server = new BridgeServer(); var emptyLegacy = new AuthorizationBroker(relay.PcId, pc);
            await server.StartAsync(settings, certificate, emptyLegacy, _ => { }, windowsRelay: relay);
            pairing = System.Text.Json.JsonSerializer.Serialize(new { endpoint = url.GetLeftPart(UriPartial.Authority), pcId = relay.PcId, pcPublicKey = Protocol.ExportPublicKey(pc), certificateSha256 = BridgeServer.CertificatePin(certificate), protocol = "bandkey-vm-phone-v1", testOnly = true }, Store.Json);
            bind.Enabled = endpoint.Enabled = false; start.Enabled = false;
            status.AppendText("\r\n中继已启动。电脑 ID：\r\n" + relay.PcId + "\r\nTLS：\r\n" + BridgeServer.CertificatePin(certificate) + "\r\n手机访问：" + url.GetLeftPart(UriPartial.Authority) + "\r\n导出配对资料，在手机实验页导入并核对指纹。");
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
        relay?.Revoke(); relay = null; var active = server; server = null;
        try { if (active != null) await active.DisposeAsync(); }
        catch (Exception error) { status.AppendText("\r\n监听器停止异常：" + error.GetType().Name + "；本轮授权已撤销。"); }
        finally
        {
            pc?.Dispose(); pc = null; certificate?.Dispose(); certificate = null; pairing = null;
            bind.Enabled = endpoint.Enabled = start.Enabled = true; stopping = false;
        }
    }
}
