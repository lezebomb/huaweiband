package dev.bandkey.bridge;

import android.app.*;
import android.content.*;
import android.os.*;
import android.widget.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.concurrent.*;
import org.json.JSONObject;

/** Foreground-only manual decisions; optional independent background BLE transport. */
public final class VmLabActivity extends Activity {
    private Identity identity;
    private TextView status;
    private Button approve,deny;
    private CheckBox wearableConfirm;
    private ScheduledExecutorService worker;
    private volatile VmRelayClient client;
    private volatile PromptGate gate;
    private volatile PromptGate.Ticket ticket;
    private volatile JSONObject pending;
    private volatile boolean active;
    private boolean configDialogOpen;
    private boolean pollingRequested;
    private volatile BlePhonePeripheral ble;
    private TextView bleStatus;
    private TextView backgroundStatus;
    private final Handler display=new Handler(Looper.getMainLooper());
    private final Runnable refreshBackground=new Runnable(){@Override public void run(){backgroundStatus.setText(VmBleTransportService.summary());display.postDelayed(this,1000);}};
    private int bleGeneration;
    private String nativeFingerprint="";
    private android.content.SharedPreferences prefs(){return getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE);}
    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);LinearLayout column=new LinearLayout(this);column.setOrientation(LinearLayout.VERTICAL);column.setPadding(28,36,28,28);ScrollView scroll=new ScrollView(this);scroll.addView(column);setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view,insets)->{
            if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(android.view.WindowInsets.Type.systemBars()|android.view.WindowInsets.Type.displayCutout());view.setPadding(bars.left,bars.top,bars.right,bars.bottom);}
            else view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        });scroll.requestApplyInsets();
        TextView intro=new TextView(this);intro.setText("VM 确认实验\n只签署隔离 VM 的一次性登录请求。默认使用手机测试按钮；可显式切换为真实手环确认。不会接收 Windows 密码。仅保持此页面打开时轮询；离开即取消本页待确认状态。可选后台模式仅保持 BLE 传输。");column.addView(intro);
        status=new TextView(this);status.setTextIsSelectable(true);column.addView(status);
        button(column,"1. 导出手机实验公钥",()->startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").putExtra(Intent.EXTRA_TITLE,"bandkey-vm-phone.bandkeypub"),1));
        button(column,"2. 导入 VM 电脑配对资料",()->{pollingRequested=false;stopWork();startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("application/json").addCategory(Intent.CATEGORY_OPENABLE),2);});
        approve=button(column,"确认本次 VM 解锁（测试按钮）",()->decide(true));deny=button(column,"拒绝本次请求",()->decide(false));approve.setEnabled(false);deny.setEnabled(false);
        wearableConfirm=new CheckBox(this);wearableConfirm.setText("使用真实手环确认（先在主界面授权并选择手环）");column.addView(wearableConfirm);
        wearableConfirm.setOnCheckedChangeListener((button,checked)->{
            boolean resume=pollingRequested;stopWork();
            show(checked?"已切换为真实手环确认，手机测试按钮停用。仅手环本次有效点击才会签名。":"已切换为手机测试按钮，原手环请求已取消。");
            if(resume)startWork();
        });
        button(column,"停止轮询并取消待确认请求",()->{pollingRequested=false;stopWork();show("已停止。已经交付的签名不能由此撤回；原生一次性及有效期检查仍适用。");});
        button(column,"启动轮询",this::startWork);
        bleStatus=new TextView(this);bleStatus.setText(R.string.vm_ble_idle);column.addView(bleStatus);
        button(column,"启动前台 BLE 传输（不需要同一 Wi-Fi）",this::startBle);
        button(column,"停止全部 BLE 传输",()->{pollingRequested=false;stopWork();closeLocalBle();VmBleTransportService.stopCurrent();bleStatus.setText(R.string.vm_ble_stopped);});
        backgroundStatus=new TextView(this);backgroundStatus.setText(VmBleTransportService.summary());column.addView(backgroundStatus);
        button(column,"启动后台 BLE 传输（最多30分钟）",this::startBackgroundBle);
        button(column,"导出本次后台传输记录",()->{
            try{VmBleTransportService.report();startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/json").putExtra(Intent.EXTRA_TITLE,"bandkey-ble-background.json"),3);}
            catch(Exception error){show("尚无可导出的后台传输记录。");}
        });
        try{
            identity=new Identity("bandkey-vm-lab-identity-v1");byte[] publicKey=identity.nativePublicKey();nativeFingerprint=Identity.hex(MessageDigest.getInstance("SHA-256").digest(publicKey));
            try(OutputStream out=openFileOutput("vm-lab-public.bandkeypub",MODE_PRIVATE)){out.write(publicKey);}
            show("手机实验公钥 SHA-256：\n"+nativeFingerprint+"\n先在 VM 安装器核对并登记该公钥。");
        }catch(Exception error){identity=null;show("实验密钥不可用："+error.getClass().getSimpleName());}
    }
    private Button button(LinearLayout column,String text,Runnable action){Button b=new Button(this);b.setText(text);b.setOnClickListener(v->action.run());column.addView(b);return b;}
    private void show(String text){runOnUiThread(()->status.setText(getString(R.string.vm_lab_status,nativeFingerprint,text)));}
    private void showCurrent(PromptGate expected,String text){runOnUiThread(()->{if(active && gate==expected)show(text);});}
    private String errorCode(Exception error){
        String message=error.getMessage();
        // Keep a bounded app-private cause chain: the top-level SSL exception alone
        // cannot distinguish certificate refusal from a closed stream or timeout.
        try{
            org.json.JSONArray causes=new org.json.JSONArray();Throwable cause=error;
            for(int depth=0;cause!=null && depth<8;depth++,cause=cause.getCause()){
                JSONObject entry=new JSONObject().put("type",cause.getClass().getSimpleName());
                if(error instanceof javax.net.ssl.SSLException && cause.getMessage()!=null){String detail=cause.getMessage();entry.put("detail",detail.substring(0,Math.min(512,detail.length())));}
                causes.put(entry);
            }
            JSONObject report=new JSONObject().put("recordedAt",System.currentTimeMillis()).put("causes",causes)
                .put("authorizationRetried",false).put("certificateValidationRelaxed",false);
            try(OutputStream out=openFileOutput("vm-transport-error.json",MODE_PRIVATE)){out.write(report.toString(2).getBytes(StandardCharsets.UTF_8));}
        }catch(Exception ignored){}
        return error instanceof IOException && message!=null && message.matches("PC HTTP [0-9]{3}")?message:error.getClass().getSimpleName();
    }
    private void stopWork(){active=false;PromptGate current=gate;if(current!=null)current.stop();pending=null;ticket=null;client=null;if(worker!=null)worker.shutdownNow();worker=null;approve.setEnabled(false);deny.setEnabled(false);}
    private void startWork(){
        pollingRequested=true;stopWork();if(identity==null)return;
        try{
            String saved=prefs().getString("pc",null);if(saved==null){show("请先导入 VM 配对资料。");return;}
            final boolean useWearable=wearableConfirm.isChecked();
            final String wearableUuid=getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getString("wearableUuid","");
            if(useWearable && wearableUuid.isEmpty()){show("请先返回主界面，授权 Wear Engine 并选择手环。");return;}
            if(useWearable && getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getBoolean("enabled",false)){show("请先停止主界面的自动桥接，再测试隔离 VM，避免两类请求混淆。");return;}
            client=new VmRelayClient(new JSONObject(saved),identity);gate=new PromptGate(()->System.currentTimeMillis()/1000,SystemClock::elapsedRealtime);active=true;worker=Executors.newSingleThreadScheduledExecutor();
            final PromptGate currentGate=gate;final VmRelayClient currentClient=client;
            final VmPollRecovery recovery=new VmPollRecovery();
            final boolean loopback="127.0.0.1".equals(new java.net.URI(currentClient.config.getString("endpoint")).getHost());
            worker.scheduleWithFixedDelay(()->{
                if(!active || gate!=currentGate)return;
                BlePhonePeripheral foregroundBle=ble;
                boolean linkAvailable=!loopback || (foregroundBle==null?(!VmBleTransportService.active() || VmBleTransportService.connected()):foregroundBle.isConnected());
                if(!recovery.ready(SystemClock.elapsedRealtime(),linkAvailable)){
                    if(!linkAvailable)showCurrent(currentGate,"等待电脑 BLE 连接；连接后自动继续轮询。尚未验证电脑 TLS 身份。");
                    return;
                }
                try{
                    JSONObject prompt=currentClient.poll();
                    final boolean recovered=recovery.succeeded();
                    runOnUiThread(()->{
                        if(!active || gate!=currentGate)return;
                        if(!currentGate.isActive(ticket)){
                            approve.setEnabled(false);deny.setEnabled(false);
                            if(recovered)show("通信正常，电脑 TLS 身份已核验；等待 VM 新请求。");
                        }else if(recovered)show("通信已恢复；当前请求仍需在原有效期内明确确认或拒绝。");
                    });
                    if(prompt==null)return;
                    WindowsGrantWire.Challenge challenge=WindowsGrantWire.read(android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP));
                    PromptGate.Ticket next=currentGate.begin(prompt.getString("challenge"),(challenge.expires-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS)/1000);if(next==null)return;
                    runOnUiThread(()->{
                        if(!active || gate!=currentGate || !currentGate.isActive(next))return;
                        pending=prompt;ticket=next;approve.setEnabled(!useWearable);deny.setEnabled(!useWearable);
                        if(!useWearable){show("VM 已锁定，收到经过核验的一次性请求。\n请在 30 秒内明确确认或拒绝；未确认不会签名。");return;}
                        show("VM 请求已核验，正在请求真实手环确认。手机测试按钮已停用。");
                        new WearableChannel(this).prompt(wearableUuid,"隔离 VM "+currentClient.config.optString("pcId").substring(0,8),currentGate,next,()->active && gate==currentGate,
                            yes->runOnUiThread(()->submitDecision(currentGate,next,prompt,currentClient,yes)),
                            message->showCurrent(currentGate,message));
                    });
                }catch(Exception error){recovery.failed(SystemClock.elapsedRealtime());showCurrent(currentGate,"通信暂时中断，自动重试："+errorCode(error));}
            },0,2,TimeUnit.SECONDS);show(useWearable?"手环确认轮询已启动。请手动锁定隔离 VM，保持手机页面在前台，再在手环确认。当前不验证真实距离。":"轮询已启动。请在 VM 内手动锁定，再在这里确认。当前不验证真实距离。");
        }catch(Exception error){stopWork();show("启动失败："+error.getClass().getSimpleName());}
    }
    private void decide(boolean yes){
        final PromptGate current=gate;final PromptGate.Ticket selected=ticket;final JSONObject prompt=pending;final VmRelayClient target=client;
        if(wearableConfirm.isChecked() || !foregroundDecision() || !active || current==null || target==null || prompt==null || !current.consume(selected)){show("没有当前有效手机测试请求，或页面不在已解锁的前台。");return;}
        submitDecision(current,selected,prompt,target,yes);
    }
    private boolean foregroundDecision(){return hasWindowFocus() && getSystemService(PowerManager.class).isInteractive() && !getSystemService(KeyguardManager.class).isKeyguardLocked();}
    private void submitDecision(PromptGate current,PromptGate.Ticket selected,JSONObject prompt,VmRelayClient target,boolean yes){
        // WearableChannel has already consumed its ticket. Consuming twice would discard a real click.
        if(!foregroundDecision() || !active || gate!=current || ticket!=selected || pending!=prompt || client!=target || !current.canSubmit(selected))return;
        approve.setEnabled(false);deny.setEnabled(false);
        try{worker.execute(()->{try{if(!active || gate!=current || !current.canSubmit(selected))return;JSONObject result=target.decide(prompt,yes);showCurrent(current,result.optBoolean("submitted")?"签名已交给原生组件；请观察 VM 是否实际进入桌面。此响应不宣称 Windows 已解锁。":"请求结果："+result.optString("code","unknown"));}catch(Exception error){showCurrent(current,"提交失败或已过期："+errorCode(error));}});}catch(RejectedExecutionException ignored){}
    }
    @Override protected void onResume(){super.onResume();display.post(refreshBackground);if(pollingRequested && !configDialogOpen && identity!=null && prefs().getString("pc",null)!=null)startWork();}
    private void closeLocalBle(){bleGeneration++;if(ble!=null)ble.close();ble=null;}
    private boolean nearbyPermissions(){
        if(Build.VERSION.SDK_INT>=31 && (checkSelfPermission(android.Manifest.permission.BLUETOOTH_ADVERTISE)!=android.content.pm.PackageManager.PERMISSION_GRANTED || checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT)!=android.content.pm.PackageManager.PERMISSION_GRANTED)){requestPermissions(new String[]{android.Manifest.permission.BLUETOOTH_ADVERTISE,android.Manifest.permission.BLUETOOTH_CONNECT},30);return false;}
        return true;
    }
    private void startBackgroundBle(){
        if(!nearbyPermissions())return;
        if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},31);return;}
        if(!getSystemService(NotificationManager.class).areNotificationsEnabled()){show("请先在系统设置中允许本应用通知，以便看到后台传输和停止入口。");return;}
        if(VmBleTransportService.active()){show("后台 BLE 已运行；重复点击不会延长30分钟期限。");return;}
        stopWork();closeLocalBle();
        try{
            if(!VmBleTransportService.requestStart(this)){show("请在手机解锁后从当前前台页面启动后台传输。");return;}
            bleStatus.setText(R.string.vm_ble_background_requested);
            if(pollingRequested)startWork();
        }catch(RuntimeException error){show("系统未允许后台传输："+error.getClass().getSimpleName());}
    }
    private void startBle(){
        if(VmBleTransportService.active()){show("请先停止后台 BLE，再启动页面前台 BLE。");return;}
        if(!nearbyPermissions())return;
        final int generation=++bleGeneration;if(ble!=null)ble.close();ble=new BlePhonePeripheral(this,19443,text->runOnUiThread(()->{if(generation==bleGeneration)bleStatus.setText(text);}));
        try{ble.start();bleStatus.setText(R.string.vm_ble_starting);}catch(Exception error){ble.close();ble=null;bleStatus.setText(getString(R.string.vm_ble_error,error.getClass().getSimpleName()));}
    }
    @Override protected void onPause(){display.removeCallbacks(refreshBackground);stopWork();closeLocalBle();if(bleStatus!=null)bleStatus.setText(R.string.vm_ble_page_paused);super.onPause();}
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);if(result!=RESULT_OK || data==null || data.getData()==null)return;
        try{
            if(request==3){try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(VmBleTransportService.report().getBytes(StandardCharsets.UTF_8));}show("后台传输记录已导出；计数不证明授权交付或 Windows 解锁。");return;}
            if(identity==null)return;
            if(request==1){try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(identity.nativePublicKey());}show("公钥已导出。它不包含私钥或 Windows 密码。");}
            if(request==2){String text;try(InputStream in=getContentResolver().openInputStream(data.getData())){text=new String(Wire.readBounded(in,8192),StandardCharsets.UTF_8);}JSONObject config=new JSONObject(text);new VmRelayClient(config,identity);
                pollingRequested=false;stopWork();configDialogOpen=true;new AlertDialog.Builder(this).setTitle("核对 VM 电脑身份").setMessage("电脑 ID：\n"+config.getString("pcId")+"\nTLS：\n"+config.getString("certificateSha256")+"\n地址："+config.getString("endpoint"))
                    .setPositiveButton("一致并保存",(dialog,which)->{configDialogOpen=false;prefs().edit().putString("pc",text).apply();startWork();})
                    .setNegativeButton("取消",(dialog,which)->{configDialogOpen=false;show("配对未更改，轮询保持停止。");})
                    .setOnCancelListener(dialog->{configDialogOpen=false;show("配对未更改，轮询保持停止。");}).show();}
        }catch(Exception error){show("公钥/配置操作失败："+error.getClass().getSimpleName());}
    }
}
