using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BandKey.Core;
using BandKey.Windows;

namespace BandKey.SessionLab;

internal sealed class SessionLabForm : Form
{
    private const int SessionChange=0x2b1,PowerBroadcast=0x218;
    [DllImport("wtsapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(IntPtr window,int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    private sealed class PreviewSession : IInteractiveSession
    {
        public bool IsInteractive=>true;
        public bool? IsLocked()=>false;
        public LockRequestResult RequestLock()=>new(false,"preview-disabled");
    }
    private readonly bool preview;
    private readonly IInteractiveSession session;
    private readonly SessionLabRun run;
    private readonly DateTimeOffset startedAt=DateTimeOffset.UtcNow;
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer timer=new(){Interval=500};
    private readonly Label state=new(){AutoSize=true,MaximumSize=new(800,0)};
    private readonly TextBox trace=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Width=790,Height=200};
    private readonly Label reportStatus=new(){AutoSize=true,MaximumSize=new(800,0)};
    private readonly string reportPath;
    private readonly int currentSession=Process.GetCurrentProcess().SessionId;
    private bool registered;
    private bool recordingStopped;
    private int registrationError,powerResumes;
    private double lastSaved;
    private string? runtimeError;
    internal SessionLabForm(string directory,bool preview)
    {
        this.preview=preview;
        session=preview?new PreviewSession():new GuestOnlySession(new WindowsSessionController(),VmNativePolicy.RequireGuest);
        run=new(session,preview);
        reportPath=Path.Combine(Path.GetFullPath(directory),"session-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");
        Text="BandKey · VM 会话与自动锁屏实验";ClientSize=new(850,560);Font=new("Microsoft YaHei UI",10);AutoScaleMode=AutoScaleMode.Dpi;
        var column=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new(18)};Controls.Add(column);
        column.Controls.Add(new Label{AutoSize=true,MaximumSize=new(800,0),Text="默认只记录本 VM 的 WTS／查询状态。\n模拟 RSSI 不代表真实手环距离；走远按钮会调用真实锁屏 API。\n本工具不解锁、不提交账户认证，也不读取密码或密钥。"});
        column.Controls.Add(state);
        var actions=new FlowLayoutPanel{AutoSize=true,WrapContents=true,MaximumSize=new(810,0)};
        void Add(string text,Action action){var button=new Button{Text=text,AutoSize=true,Enabled=!preview,Margin=new(3)};button.Click+=(_,_)=>action();actions.Controls.Add(button);}
        Add("模拟抖动（不应锁屏）",()=>StartScenario(SignalScenario.Jitter));
        Add("模拟走远（会锁定此 VM）",()=>StartScenario(SignalScenario.Away));
        Add("停止模拟",()=>{run.StopScenario();RefreshStatus();SaveReport();});column.Controls.Add(actions);
        column.Controls.Add(trace);column.Controls.Add(reportStatus);
        Shown+=(_,_)=>{if(preview){run.Observe(false,ObservationSource.Query,0);RefreshStatus();return;}Poll();timer.Start();};
        timer.Tick+=(_,_)=>Poll();
        FormClosing+=(_,_)=>{timer.Stop();recordingStopped=true;run.StopScenario();SaveReport();};
        FormClosed+=(_,_)=>timer.Dispose();
        RefreshStatus();
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);if(preview)return;
        registered=WTSRegisterSessionNotification(Handle,0);
        if(!registered)registrationError=Marshal.GetLastWin32Error();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if(registered){WTSUnRegisterSessionNotification(Handle);registered=false;}
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message message)
    {
        if(!preview && !recordingStopped && message.Msg==SessionChange && message.LParam.ToInt64()==currentSession && message.WParam.ToInt64() is 7 or 8)
        {
            run.Observe(message.WParam.ToInt64()==7,ObservationSource.Notification,clock.Elapsed.TotalMilliseconds);
            RefreshStatus();SaveReport();
        }
        if(!preview && !recordingStopped && message.Msg==PowerBroadcast && message.WParam.ToInt64() is 7 or 0x12)
        {
            powerResumes++;run.StopScenario();SaveReport();
        }
        base.WndProc(ref message);
    }
    private void StartScenario(SignalScenario scenario)
    {
        if(!run.StartScenario(scenario,clock.Elapsed.TotalMilliseconds))reportStatus.Text="当前会话未确认已登录，未开始模拟。";
        RefreshStatus();SaveReport();
    }
    private void Poll()
    {
        try
        {
            double now=clock.Elapsed.TotalMilliseconds;
            run.Observe(session.IsLocked(),ObservationSource.Query,now);run.Tick(now);RefreshStatus();
            if(now-lastSaved>=5000){SaveReport();lastSaved=now;}
        }
        catch(Exception error){runtimeError=error.GetType().Name;run.StopScenario();timer.Stop();recordingStopped=true;SaveReport();reportStatus.Text="记录已停止："+runtimeError;}
    }
    private void RefreshStatus()
    {
        state.Text=$"WTS 通知：{(registered?"已注册":"未注册／预览")}；场景：{run.Scenario}\n模拟距离：{run.Presence.State}；滤波：{run.Presence.FilteredRssi:0.0}；就绪：{run.Presence.Armed}\n锁屏请求：{run.Evidence.Attempts.Length}；观察到锁定后解锁：通知 {run.Evidence.NotificationUnlocksAfterLock}／查询 {run.Evidence.QueryUnlocksAfterLock}\n观察到解锁不等于已证明由 BandKey 解锁。";
        trace.Lines=run.Evidence.Events.TakeLast(18).Select(e=>$"{e.ElapsedMilliseconds/1000:0.0}s | {e.Source} | {e.Locked switch{true=>"Locked",false=>"Unlocked",_=>"Unknown"}}").ToArray();
        if(preview)reportStatus.Text="离线布局预览，不注册 WTS、不调用锁屏、不写会话报告。";
    }
    private void SaveReport()
    {
        if(preview)return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var report=new{startedAt,elapsedMilliseconds=clock.Elapsed.TotalMilliseconds,evidence=run.Evidence.Report(),recordingStopped,wtsNotificationRegistered=registered,wtsRegistrationError=registrationError,powerResumes,runtimeError,
                inputSource=run.ScenarioEverStarted?"simulated-rssi":"none",realRssiTested=false,scenario=run.Scenario};
            File.WriteAllText(reportPath+".tmp",JsonSerializer.Serialize(report,Program.Json));File.Move(reportPath+".tmp",reportPath,true);
            reportStatus.Text="自动保存（不含账户、SID、凭据、密钥或请求正文）：\n"+reportPath;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){reportStatus.Text="报告保存失败："+error.GetType().Name;}
    }
}
