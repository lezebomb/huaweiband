using System.Net;

namespace BandKey.Windows;

/// <summary>Explicit, user-session transport helper. Never enables an authentication backend.</summary>
public sealed class BleBridgeForm : Form
{
    private readonly Store store;
    private readonly TextBox target=new(){Width=260};
    private readonly NumericUpDown minutes=new(){Minimum=1,Maximum=120,Value=30,Width=80};
    private readonly Button start=new(){Text="连接手机 BLE",AutoSize=true},stop=new(){Text="停止并断开",AutoSize=true,Enabled=false};
    private readonly TextBox status=new(){Multiline=true,ReadOnly=true,Width=650,Height=180,ScrollBars=ScrollBars.Vertical};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=500};
    private CancellationTokenSource? cancellation;private BlePhoneTransport? tunnel;private Task? running;private bool closing;
    private sealed record Settings(string GuestAddress="",int Minutes=30);
    public BleBridgeForm(Store store)
    {
        this.store=store;Text="BandKey · 手机 BLE 通信实验";ClientSize=new(710,400);Font=new("Microsoft YaHei UI",10);AutoScaleMode=AutoScaleMode.Dpi;
        var column=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new(18),AutoScroll=true};Controls.Add(column);
        column.Controls.Add(new Label{Text="无需同一 Wi-Fi。先在手机实验页启动 BLE，保持页面打开。\n填写 VM 的私网地址；VM 手机中继须已监听端口 19443。\n这里显示传输状态，实际解锁仍由 VM 独立认证。",AutoSize=true,MaximumSize=new(660,0)});
        var fields=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false};fields.Controls.Add(new Label{Text="VM 私网 IP",AutoSize=true,Padding=new(0,6,0,0)});fields.Controls.Add(target);fields.Controls.Add(new Label{Text="最长分钟",AutoSize=true,Padding=new(0,6,0,0)});fields.Controls.Add(minutes);column.Controls.Add(fields);
        var actions=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false};actions.Controls.Add(start);actions.Controls.Add(stop);column.Controls.Add(actions);column.Controls.Add(status);
        try{var saved=store.Read<Settings>("transport.json");if(saved!=null){target.Text=saved.GuestAddress;minutes.Value=Math.Clamp(saved.Minutes,1,120);}}catch(Exception){status.Text="上次设置不可读取，请重新输入。";}
        if(status.Text.Length==0)status.Text="尚未连接。先在手机启动前台 BLE，再连接本助手；连接成功后仍须验证 TLS 和本次请求。";
        start.Click+=async(_,_)=>await StartTransport();stop.Click+=async(_,_)=>await StopTransport();
        timer.Tick+=(_,_)=>RefreshStatus();timer.Start();
        FormClosing+=async(_,args)=>{if(closing)return;if(running!=null && !running.IsCompleted){args.Cancel=true;await StopTransport();closing=true;Close();}};
        FormClosed+=(_,_)=>timer.Dispose();
    }
    private void Log(string code){try{store.Log("ble-transport",code);}catch(IOException){}catch(UnauthorizedAccessException){}}
    private void RefreshStatus(){if(tunnel!=null)status.Text=$"阶段：{tunnel.Stage}；连接尝试：{tunnel.Attempts}\r\nMTU：{tunnel.Mtu}；完成数据流：{tunnel.Streams}\r\n发往手机：{tunnel.SentBytes} 字节；来自手机：{tunnel.ReceivedBytes} 字节\r\n最近故障：{tunnel.Failure?.Code??"无"}；未据此判断手环距离或 Windows 解锁。";}
    private async Task StartTransport()
    {
        if(running is {IsCompleted:false})return;
        if(!IPAddress.TryParse(target.Text.Trim(),out var address) || !LoopbackTcpTunnel.IsLocalTarget(address) || IPAddress.IsLoopback(address)){status.Text="请输入 VM 的 RFC1918 私网 IPv4 地址。";return;}
        try{store.Write("transport.json",new Settings(address.ToString(),(int)minutes.Value));}catch(Exception error){status.Text="设置保存失败："+error.GetType().Name;return;}
        cancellation=new();tunnel=new();start.Enabled=target.Enabled=minutes.Enabled=false;stop.Enabled=true;Log("start");
        running=RunTransport(tunnel,address,cancellation.Token);await running;
    }
    private async Task RunTransport(BlePhoneTransport current,IPAddress address,CancellationToken token)
    {
        string result;
        try{await current.RunAsync(address,19443,TimeSpan.FromMinutes((int)minutes.Value),cancellation:token);result=$"通信已停止；连接尝试 {current.Attempts}，故障记录 {current.Failures.Length}；最近故障：{current.Failure?.Code??"无"}。";Log("stopped");}
        catch(OperationCanceledException){result="已取消连接。";Log("cancelled");}
        catch(Exception error){var failure=current.Failure??BleTransportFailure.From(error);result="通信停止："+failure.Code+"（"+current.Stage+"）。检查手机前台 BLE、蓝牙状态和 VM 监听，之后重新连接。";Log(failure.Code);}
        finally{foreach(var failure in current.Failures)Log($"attempt-{failure.Attempt}:{failure.Failure.Code}");tunnel=null;cancellation?.Dispose();cancellation=null;start.Enabled=target.Enabled=minutes.Enabled=true;stop.Enabled=false;}
        status.Text=result;
    }
    private async Task StopTransport(){cancellation?.Cancel();var active=running;if(active!=null)await active;}
}
