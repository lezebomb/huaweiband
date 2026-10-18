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

/** Foreground-only manual testing. It has no Wear Engine dependency or boot receiver. */
public final class VmLabActivity extends Activity {
    private Identity identity;
    private TextView status;
    private Button approve,deny;
    private ScheduledExecutorService worker;
    private volatile VmRelayClient client;
    private volatile PromptGate gate;
    private volatile PromptGate.Ticket ticket;
    private volatile JSONObject pending;
    private volatile boolean active;
    private boolean configDialogOpen;
    private boolean pollingRequested;
    private String nativeFingerprint="";
    private android.content.SharedPreferences prefs(){return getSharedPreferences("bandkey-vm-phone-lab",MODE_PRIVATE);}
    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);LinearLayout column=new LinearLayout(this);column.setOrientation(LinearLayout.VERTICAL);column.setPadding(28,36,28,28);ScrollView scroll=new ScrollView(this);scroll.addView(column);setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view,insets)->{
            if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(android.view.WindowInsets.Type.systemBars()|android.view.WindowInsets.Type.displayCutout());view.setPadding(bars.left,bars.top,bars.right,bars.bottom);}
            else view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        });scroll.requestApplyInsets();
        TextView intro=new TextView(this);intro.setText("VM 手机确认实验\n只签署隔离 VM 的一次性登录请求。确认必须手动点击，不调用 Wear Engine，不接收 Windows 密码。仅保持此页面打开时轮询；离开即取消本页待确认状态。");column.addView(intro);
        status=new TextView(this);status.setTextIsSelectable(true);column.addView(status);
        button(column,"1. 导出手机实验公钥",()->startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").putExtra(Intent.EXTRA_TITLE,"bandkey-vm-phone.bandkeypub"),1));
        button(column,"2. 导入 VM 电脑配对资料",()->{pollingRequested=false;stopWork();startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("application/json").addCategory(Intent.CATEGORY_OPENABLE),2);});
        approve=button(column,"确认本次 VM 解锁（测试按钮）",()->decide(true));deny=button(column,"拒绝本次请求",()->decide(false));approve.setEnabled(false);deny.setEnabled(false);
        button(column,"停止轮询并取消待确认请求",()->{pollingRequested=false;stopWork();show("已停止。已经交付的签名不能由此撤回；原生一次性及有效期检查仍适用。");});
        button(column,"启动轮询",this::startWork);
        try{
            identity=new Identity("bandkey-vm-lab-identity-v1");byte[] publicKey=identity.nativePublicKey();nativeFingerprint=Identity.hex(MessageDigest.getInstance("SHA-256").digest(publicKey));
            try(OutputStream out=openFileOutput("vm-lab-public.bandkeypub",MODE_PRIVATE)){out.write(publicKey);}
            show("手机实验公钥 SHA-256：\n"+nativeFingerprint+"\n先在 VM 安装器核对并登记该公钥。");
        }catch(Exception error){identity=null;show("实验密钥不可用："+error.getClass().getSimpleName());}
    }
    private Button button(LinearLayout column,String text,Runnable action){Button b=new Button(this);b.setText(text);b.setOnClickListener(v->action.run());column.addView(b);return b;}
    private void show(String text){runOnUiThread(()->status.setText("手机实验公钥 SHA-256：\n"+nativeFingerprint+"\n\n"+text));}
    private void showCurrent(PromptGate expected,String text){runOnUiThread(()->{if(active && gate==expected)show(text);});}
    private static String errorCode(Exception error){String message=error.getMessage();return error instanceof IOException && message!=null && message.matches("PC HTTP [0-9]{3}")?message:error.getClass().getSimpleName();}
    private void stopWork(){active=false;PromptGate current=gate;if(current!=null)current.stop();pending=null;ticket=null;client=null;if(worker!=null)worker.shutdownNow();worker=null;approve.setEnabled(false);deny.setEnabled(false);}
    private void startWork(){
        pollingRequested=true;stopWork();if(identity==null)return;
        try{
            String saved=prefs().getString("pc",null);if(saved==null){show("请先导入 VM 配对资料。");return;}
            client=new VmRelayClient(new JSONObject(saved),identity);gate=new PromptGate(()->System.currentTimeMillis()/1000,SystemClock::elapsedRealtime);active=true;worker=Executors.newSingleThreadScheduledExecutor();
            final PromptGate currentGate=gate;final VmRelayClient currentClient=client;
            worker.scheduleWithFixedDelay(()->{
                if(!active || gate!=currentGate)return;
                try{
                    JSONObject prompt=currentClient.poll();
                    runOnUiThread(()->{if(active && gate==currentGate && !currentGate.isActive(ticket)){approve.setEnabled(false);deny.setEnabled(false);}});
                    if(prompt==null)return;
                    WindowsGrantWire.Challenge challenge=WindowsGrantWire.read(android.util.Base64.decode(prompt.getString("challenge"),android.util.Base64.NO_WRAP));
                    PromptGate.Ticket next=currentGate.begin(prompt.getString("challenge"),(challenge.expires-WindowsGrantWire.FILETIME_UNIX_MILLISECONDS)/1000);if(next==null)return;
                    runOnUiThread(()->{if(active && gate==currentGate && currentGate.isActive(next)){pending=prompt;ticket=next;approve.setEnabled(true);deny.setEnabled(true);show("VM 已锁定，收到经过核验的一次性请求。\n请在 30 秒内明确确认或拒绝；未确认不会签名。");}});
                }catch(Exception error){showCurrent(currentGate,"等待连接或新请求："+errorCode(error));}
            },0,2,TimeUnit.SECONDS);show("轮询已启动。请在 VM 内手动锁定，再在这里确认。当前不验证真实距离。");
        }catch(Exception error){stopWork();show("启动失败："+error.getClass().getSimpleName());}
    }
    private void decide(boolean yes){
        final PromptGate current=gate;final PromptGate.Ticket selected=ticket;final JSONObject prompt=pending;final VmRelayClient target=client;
        if(!active || current==null || target==null || prompt==null || !current.consume(selected)){show("没有当前有效请求。");return;}
        approve.setEnabled(false);deny.setEnabled(false);
        try{worker.execute(()->{try{if(!active || gate!=current || !current.canSubmit(selected))return;JSONObject result=target.decide(prompt,yes);showCurrent(current,result.optBoolean("submitted")?"签名已交给原生组件；请观察 VM 是否实际进入桌面。此响应不宣称 Windows 已解锁。":"请求结果："+result.optString("code","unknown"));}catch(Exception error){showCurrent(current,"提交失败或已过期："+errorCode(error));}});}catch(RejectedExecutionException ignored){}
    }
    @Override protected void onResume(){super.onResume();if(pollingRequested && !configDialogOpen && identity!=null && prefs().getString("pc",null)!=null)startWork();}
    @Override protected void onPause(){stopWork();super.onPause();}
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);if(result!=RESULT_OK || data==null || data.getData()==null || identity==null)return;
        try{
            if(request==1){try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(identity.nativePublicKey());}show("公钥已导出。它不包含私钥或 Windows 密码。");}
            if(request==2){String text;try(InputStream in=getContentResolver().openInputStream(data.getData())){text=new String(Wire.readBounded(in,8192),StandardCharsets.UTF_8);}JSONObject config=new JSONObject(text);new VmRelayClient(config,identity);
                pollingRequested=false;stopWork();configDialogOpen=true;new AlertDialog.Builder(this).setTitle("核对 VM 电脑身份").setMessage("电脑 ID：\n"+config.getString("pcId")+"\nTLS：\n"+config.getString("certificateSha256")+"\n地址："+config.getString("endpoint"))
                    .setPositiveButton("一致并保存",(dialog,which)->{configDialogOpen=false;prefs().edit().putString("pc",text).apply();startWork();})
                    .setNegativeButton("取消",(dialog,which)->{configDialogOpen=false;show("配对未更改，轮询保持停止。");})
                    .setOnCancelListener(dialog->{configDialogOpen=false;show("配对未更改，轮询保持停止。");}).show();}
        }catch(Exception error){show("公钥/配置操作失败："+error.getClass().getSimpleName());}
    }
}
