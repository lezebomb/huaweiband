package dev.bandkey.bridge;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.provider.Settings;
import android.widget.*;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.auth.AuthCallback;
import com.huawei.wearengine.auth.Permission;
import com.huawei.wearengine.device.Device;
import java.io.*;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private TextView output;
    private Identity identity;
    private BandAdvertisementProbe advertisementProbe;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final Runnable refresh=new Runnable(){public void run(){render();handler.postDelayed(this,2000);}};
    private SharedPreferences prefs(){return getSharedPreferences(BridgeService.PREFS,MODE_PRIVATE);}
    @Override public void onCreate(Bundle saved){
        super.onCreate(saved);
        LinearLayout column=new LinearLayout(this);column.setOrientation(LinearLayout.VERTICAL);column.setPadding(28,30,28,20);
        ScrollView scroll=new ScrollView(this);scroll.addView(column);setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view,insets)->{
            if(Build.VERSION.SDK_INT>=30){
                android.graphics.Insets bars=insets.getInsets(android.view.WindowInsets.Type.systemBars()|android.view.WindowInsets.Type.displayCutout());
                view.setPadding(bars.left,bars.top,bars.right,bars.bottom);
                android.view.WindowInsetsController controller=view.getWindowInsetsController();
                if(controller!=null){int light=android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;controller.setSystemBarsAppearance(light,light);}
            }else{
                view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
                getWindow().getDecorView().setSystemUiVisibility(android.view.View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|android.view.View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
            }
            return insets;
        });
        scroll.requestApplyInsets();
        TextView title=new TextView(this);title.setText(R.string.screen_title);title.setTextSize(24);column.addView(title);
        TextView intro=new TextView(this);intro.setText("一次性配置后由手环确认。当前 Windows 登录后端尚未提供，真实解锁仍待实现。\n请保持运动健康连接；此程序不会重置或重新配对手环。");column.addView(intro);
        output=new TextView(this);output.setTextIsSelectable(true);column.addView(output);
        button(column,"1. 导入电脑配对资料",()->{Intent i=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("application/json").addCategory(Intent.CATEGORY_OPENABLE);startActivityForResult(i,10);});
        button(column,"2. 导出手机公钥",()->{Intent i=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/json").putExtra(Intent.EXTRA_TITLE,"bandkey-phone.json");startActivityForResult(i,11);});
        button(column,"3. 授权 Wear Engine 并选择手环",this::authorize);
        button(column,"诊断连接与设备能力",()->new WearDiagnostics(this,this::show).run());
        button(column,"手环按钮测试（不会解锁电脑）",()->startActivity(new Intent(this,WearTestActivity.class)));
        button(column,"诊断目标手环广播（30 秒）",this::probeAdvertisement);
        button(column,"导出诊断 JSON",()->startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/json").putExtra(Intent.EXTRA_TITLE,"bandkey-diagnostics.json"),12));
        button(column,"4. 启动自动桥接",()->{
            if(prefs().getString("pc",null)==null || prefs().getString("wearableUuid",null)==null){show("请先导入电脑资料并选择手环");return;}
            if(Build.VERSION.SDK_INT>=31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.BLUETOOTH_CONNECT},20);return;}
            if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},21);return;}
            prefs().edit().putBoolean("enabled",true).apply();
            try{startForegroundService(new Intent(this,BridgeService.class));}
            catch(RuntimeException e){BridgeService.stop(this);show("系统未允许启动桥接："+e.getClass().getSimpleName());}
        });
        button(column,"停止并关闭开机启动",()->{BridgeService.stop(this);show("桥接已停止，旧请求不再接受点击");});
        button(column,"ColorOS 后台设置",()->{startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,android.net.Uri.parse("package:"+getPackageName())));});
        button(column,"VM 确认实验（手机或真实手环）",()->startActivity(new Intent(this,VmLabActivity.class)));
        try{identity=new Identity();}catch(Exception e){show("密钥初始化失败："+e.getClass().getSimpleName());}
        if(getIntent().getBooleanExtra("bandkeyVmLab",false))startActivity(new Intent(this,VmLabActivity.class));
    }
    private void authorize(){
        if(Build.VERSION.SDK_INT>=31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.BLUETOOTH_CONNECT},20);return;}
        new AlertDialog.Builder(this).setTitle("手环权限的用途")
            .setMessage("设备基础信息：查询运动健康已配对设备、连接状态和通知能力，用于选定你的手环。\n\n消息通知：向选定手环发送电脑的一次性解锁确认，并处理确认或拒绝按钮。发送成功本身不会授权。\n\n本程序不读取心率、运动记录或联系人。电脑配对数据和设备选择只保存在本机。可随时停止桥接，并在电脑撤销手机公钥。")
            .setPositiveButton("继续申请权限",(dialog,which)->requestWearPermission()).setNegativeButton("取消",null).show();
    }
    private void requestWearPermission(){
        if(!WearSetup.configured(this)){show("尚未配置华为 App ID：需先开通账号服务和 Wear Engine 的设备基础信息、消息通知，再重新构建 APK。");return;}
        if("未安装".equals(WearSetup.version(this,"com.huawei.hwid"))){show("未检测到 HMS Core。华为文档要求非华为手机安装 HMS Core；请从华为官方来源安装后重试。");return;}
        try {
            HiWear.getAuthClient(this).requestPermission(new AuthCallback(){
                @Override public void onOk(Permission[] permissions){runOnUiThread(()->selectDevice());}
                @Override public void onCancel(){runOnUiThread(()->show("Wear Engine 用户授权被取消"));}
            },Permission.DEVICE_MANAGER,Permission.NOTIFY).addOnFailureListener(e->show("Wear Engine 授权调用失败："+WearErrors.describe(e)));
        }catch(Exception e){show("Wear Engine 不可用："+WearErrors.describe(e));}
    }
    private void probeAdvertisement(){
        if(Build.VERSION.SDK_INT<31){show("广播诊断目前要求 Android 12 或更新版本");return;}
        if(checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN)!=PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{Manifest.permission.BLUETOOTH_SCAN},22);return;}
        if(advertisementProbe!=null)advertisementProbe.stop();
        EditText address=new EditText(this);address.setSingleLine(true);address.setText(R.string.diagnostic_band_address);address.setHint("AA:BB:CC:DD:EE:FF");
        new AlertDialog.Builder(this).setTitle("只扫描此地址，不连接或配对").setView(address)
            .setPositiveButton("扫描 30 秒",(dialog,which)->{advertisementProbe=new BandAdvertisementProbe(this,address.getText().toString().trim(),this::show);advertisementProbe.start();})
            .setNegativeButton("取消",null).show();
    }
    private void selectDevice(){
        HiWear.getDeviceClient(this).getCommonDevice().addOnSuccessListener(devices->{
            runOnUiThread(()->{
                if(devices.isEmpty()){show("运动健康未返回设备。检查服务申请、签名指纹和手机兼容性。");return;}
                String[] names=new String[devices.size()];for(int i=0;i<names.length;i++){Device d=devices.get(i);names[i]=d.getName()+" / "+d.getSoftwareVersion()+" / Notify="+d.getNotifyCapability();}
                new AlertDialog.Builder(this).setTitle("选择华为手环 11 Pro").setItems(names,(dialog,index)->{
                    Device d=devices.get(index);BridgeService.stop(this);prefs().edit().putString("wearableUuid",d.getUuid()).putString("wearableName",d.getName()).apply();show("已选 "+d.getName()+"；请重新启动桥接。Notify="+d.getNotifyCapability()+"（支持="+Device.DEVICE_CAPABILITY_SUPPORT+"）");
                }).show();
            });
        }).addOnFailureListener(e->show("设备查询失败："+WearErrors.describe(e)));
    }
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);if(result!=RESULT_OK || data==null || data.getData()==null)return;
        try {
            if(request==10){
                String text;try(InputStream input=getContentResolver().openInputStream(data.getData())){byte[] bytes=Wire.readBounded(input,8192);text=new String(bytes,StandardCharsets.UTF_8);}
                JSONObject config=new JSONObject(text);new BridgeClient(config,identity);
                final String saved=text;
                new AlertDialog.Builder(this).setTitle("核对电脑身份").setMessage("请在电脑核对这两个完整指纹：\n电脑公钥\n"+config.getString("pcId")+"\nTLS 证书\n"+config.getString("certificateSha256")+"\n地址 "+config.getString("endpoint"))
                    .setPositiveButton("核对一致并保存",(dialog,which)->{BridgeService.stop(this);prefs().edit().putString("pc",saved).apply();render();}).setNegativeButton("取消",null).show();
            }
            if(request==11){try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(identity.export().toString(2).getBytes(StandardCharsets.UTF_8));}show("手机公钥已导出，请在 Windows 中导入并核对指纹。");}
            if(request==12){
                JSONObject report=new JSONObject().put("setup",WearSetup.report(this));
                for(String name:new String[]{"wear-diagnostics.json","band-advertisements.json"}){
                    try(InputStream in=openFileInput(name)){report.put(name,new JSONObject(new String(Wire.readBounded(in,262144),StandardCharsets.UTF_8)));}
                    catch(FileNotFoundException e){report.put(name,"尚未运行");}
                }
                try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(report.toString(2).getBytes(StandardCharsets.UTF_8));}
                show("诊断已导出；仅包含环境与目标设备探测结果，不含私钥。");
            }
        }catch(Exception e){show("配置操作失败："+e.getClass().getSimpleName());}
    }
    private void render(){try{output.setText(getString(R.string.status_format,identity==null?"未就绪":identity.deviceId(),prefs().getString("wearableName","未选择"),prefs().getBoolean("enabled",false)?"已启用":"未启用",prefs().getString("status","未启动")));}catch(Exception e){output.setText(R.string.status_error);}}
    private void show(String text){runOnUiThread(()->{BridgeService.status(this,text);Toast.makeText(this,text,Toast.LENGTH_LONG).show();render();});}
    private void button(LinearLayout layout,String text,Runnable clicked){Button b=new Button(this);b.setText(text);b.setOnClickListener(v->clicked.run());layout.addView(b);}
    @Override protected void onResume(){super.onResume();handler.post(refresh);}
    @Override protected void onPause(){handler.removeCallbacks(refresh);super.onPause();}
    @Override protected void onDestroy(){if(advertisementProbe!=null)advertisementProbe.stop();super.onDestroy();}
}
