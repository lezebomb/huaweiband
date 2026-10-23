package dev.bandkey.bridge;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.WindowManager;
import android.widget.*;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import org.json.JSONObject;

/** Real Wear Engine notification diagnostics. This activity has no PC client or signing key. */
public final class WearTestActivity extends Activity {
    private TextView status;
    private Button send;
    private final Handler display=new Handler(Looper.getMainLooper());
    private volatile PromptGate gate;
    private PromptGate.Ticket ticket;
    private volatile boolean active;
    private long started,deadline;
    private JSONObject report;
    private final Runnable expire=()->{
        if(active && gate!=null && gate.isActive(ticket)){gate.cancel(ticket);active=false;record("expired","30 秒已过期，旧通知按钮不会被接受。可以发送新测试。");send.setEnabled(true);}
        else if(active){active=false;record("expired","测试已结束。旧通知按钮不会被接受。");send.setEnabled(true);}
    };
    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        LinearLayout column=new LinearLayout(this);column.setOrientation(LinearLayout.VERTICAL);column.setPadding(28,50,28,28);
        ScrollView scroll=new ScrollView(this);scroll.addView(column);setContentView(scroll);
        TextView intro=new TextView(this);intro.setText("真实手环按钮测试\n仅向已经选择的手环发送一次 30 秒双按钮通知。不会签名、连接电脑或提交 Windows 认证。请在手环点击“一键解锁”（这里只记录确认）或“拒绝”。离开此页面会取消测试。");column.addView(intro);
        status=new TextView(this);status.setTextIsSelectable(true);column.addView(status);
        send=new Button(this);send.setText("发送一次手环双按钮测试");send.setOnClickListener(v->startTest());column.addView(send);
        Button cancel=new Button(this);cancel.setText("取消本次测试");cancel.setOnClickListener(v->cancelTest("已取消测试，旧按钮不再接受。"));column.addView(cancel);
        String selected=getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getString("wearableName","");
        status.setText(selected.isEmpty()?"请先返回主界面，授权并选择手环。":"已选手环："+selected+"\n等待发送测试通知。");
    }
    private void startTest(){
        String uuid=getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getString("wearableUuid","");
        if(uuid.isEmpty()){status.setText("请先在主界面授权并选择手环。");return;}
        if(getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE).getBoolean("enabled",false)){status.setText("请先在主界面停止自动桥接，避免混淆测试通知和真实请求。");return;}
        if(gate!=null)gate.stop();display.removeCallbacks(expire);
        gate=new PromptGate(()->System.currentTimeMillis()/1000,SystemClock::elapsedRealtime);
        started=System.currentTimeMillis();deadline=SystemClock.elapsedRealtime()+30000;
        ticket=gate.begin(UUID.randomUUID().toString(),started/1000+30);active=ticket!=null;
        report=new JSONObject();record("started","正在向所选手环发送测试通知。");
        if(!active)return;
        send.setEnabled(false);
        final PromptGate current=gate;
        new WearableChannel(this).prompt(uuid,"BandKey 按钮测试","仅测试，不解锁电脑 · 30 秒内有效",current,ticket,()->active && gate==current,
            approved->runOnUiThread(()->{if(!active || gate!=current || !current.canSubmit(ticket))return;active=false;display.removeCallbacks(expire);send.setEnabled(true);record(approved?"confirmed":"denied",approved?"已收到真实手环确认回调。此测试未签名，也未解锁电脑。":"已收到真实手环拒绝回调。电脑认证未被调用。");}),
            value->runOnUiThread(()->{if(!active || gate!=current)return;record("sdk-status",value);if(!current.isActive(ticket)){active=false;display.removeCallbacks(expire);send.setEnabled(true);}}));
        display.postDelayed(expire,Math.max(1,deadline-SystemClock.elapsedRealtime()));
    }
    private void record(String stage,String message){
        status.setText(message);
        try{
            if(report==null)report=new JSONObject();
            report.put("testOnly",true).put("windowsAuthenticationSubmitted",false).put("signedAuthorization",false)
                .put("startedAt",started).put("updatedAt",System.currentTimeMillis()).put("stage",stage).put("status",message);
            try(FileOutputStream out=openFileOutput("wear-notify-test.json",MODE_PRIVATE)){out.write(report.toString(2).getBytes(StandardCharsets.UTF_8));}
        }catch(Exception ignored){}
    }
    private void cancelTest(String message){boolean wasActive=active;active=false;display.removeCallbacks(expire);if(gate!=null)gate.stop();if(send!=null)send.setEnabled(true);if(wasActive)record("cancelled",message);}
    @Override protected void onPause(){cancelTest("页面已离开，测试取消。旧通知按钮不再接受。");super.onPause();}
}
