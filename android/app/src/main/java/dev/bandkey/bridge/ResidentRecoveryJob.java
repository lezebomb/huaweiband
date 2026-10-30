package dev.bandkey.bridge;

import android.app.*;
import android.app.job.*;
import android.content.*;
import android.os.*;
import java.util.List;

/** Best-effort system cleanup recovery, with no exact alarms or signing state. */
public final class ResidentRecoveryJob extends JobService {
    private static final int JOB=16971;
    private static final String PREFS="bandkey-host-resident";
    static void explicitStart(Context context){
        context.getSharedPreferences(PREFS,MODE_PRIVATE).edit().putBoolean("enabled",true)
            .putLong("enabledAt",System.currentTimeMillis()).commit();
        schedule(context);
    }
    static void stop(Context context){
        context.getSharedPreferences(PREFS,MODE_PRIVATE).edit().putBoolean("enabled",false).commit();
        context.getSystemService(JobScheduler.class).cancel(JOB);
    }
    static boolean permitted(Context context){
        SharedPreferences saved=context.getSharedPreferences(PREFS,MODE_PRIVATE);
        boolean userStopped=false;long exitAt=0;
        if(Build.VERSION.SDK_INT>=30){
            try{
                List<ApplicationExitInfo> exits=context.getSystemService(ActivityManager.class)
                    .getHistoricalProcessExitReasons(context.getPackageName(),0,16);
                // A package update after a user stop must not hide that stop.
                for(ApplicationExitInfo exit:exits)if(exit.getReason()==ApplicationExitInfo.REASON_USER_REQUESTED){
                    userStopped=true;exitAt=Math.max(exitAt,exit.getTimestamp());
                }
            }catch(RuntimeException error){return false;}
        }
        boolean allowed=ResidentResumePolicy.allowed(saved.getBoolean("enabled",false),saved.getLong("enabledAt",0),userStopped,exitAt);
        if(!allowed && userStopped)stop(context);
        return allowed;
    }
    static void schedule(Context context){
        if(!context.getSharedPreferences(PREFS,MODE_PRIVATE).getBoolean("enabled",false))return;
        try{
            JobScheduler scheduler=context.getSystemService(JobScheduler.class);
            if(scheduler.getPendingJob(JOB)!=null)return;
            scheduler.schedule(new JobInfo.Builder(JOB,new ComponentName(context,ResidentRecoveryJob.class))
                .setPeriodic(15*60*1000L).setPersisted(true).build());
        }catch(RuntimeException ignored){}
    }
    static boolean recover(Context context,String source){
        if(VmWearConfirmService.active())return true;
        if(!permitted(context) || !context.getSystemService(PowerManager.class).isIgnoringBatteryOptimizations(context.getPackageName()))return false;
        boolean requested=VmWearConfirmService.resumeResident(context);
        context.getSharedPreferences(PREFS,MODE_PRIVATE).edit().putString("lastRecoverySource",source)
            .putLong("lastRecoveryAt",System.currentTimeMillis()).putBoolean("lastRecoveryRequested",requested).apply();
        return requested;
    }
    @Override public boolean onStartJob(JobParameters parameters){
        recover(this,"system-job");
        return false;
    }
    @Override public boolean onStopJob(JobParameters parameters){return false;}
}
