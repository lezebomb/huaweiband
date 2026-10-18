package dev.bandkey.bridge;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import org.json.JSONObject;

/** Readiness checks use public package metadata, never Huawei Health private data. */
final class WearSetup {
    private WearSetup(){}
    static String appId(Context context){
        try{
            ApplicationInfo info=context.getPackageManager().getApplicationInfo(context.getPackageName(),PackageManager.GET_META_DATA);
            Object value=info.metaData==null?null:info.metaData.get("com.huawei.hms.client.appid");
            return value==null?"":value.toString();
        }catch(PackageManager.NameNotFoundException e){return "";}
    }
    static boolean configured(Context context){return appId(context).matches("[0-9]+");}
    static String version(Context context,String packageName){
        try{return context.getPackageManager().getPackageInfo(packageName,0).versionName;}
        catch(PackageManager.NameNotFoundException e){return "未安装";}
    }
    static JSONObject report(Context context){
        JSONObject result=new JSONObject();
        try{result.put("appIdConfigured",configured(context)).put("healthVersion",version(context,"com.huawei.health"))
            .put("hmsCoreVersion",version(context,"com.huawei.hwid"));}catch(Exception ignored){}
        return result;
    }
}
