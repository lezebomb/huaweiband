package dev.bandkey.bridge;
import android.content.*;
public final class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent){
        if(!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(intent.getAction()))return;
        if(VmWearConfirmService.resumeResident(context))return;
        if(context.getSharedPreferences(BridgeService.PREFS,Context.MODE_PRIVATE).getBoolean("enabled",false)){
            try{context.startForegroundService(new Intent(context,BridgeService.class));}
            catch(RuntimeException e){BridgeService.status(context,"系统阻止开机启动，请打开 BandKey 一次："+e.getClass().getSimpleName());}
        }
    }
}
