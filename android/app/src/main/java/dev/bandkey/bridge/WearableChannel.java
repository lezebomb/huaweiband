package dev.bandkey.bridge;

import android.content.Context;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.device.Device;
import com.huawei.wearengine.notify.Action;
import com.huawei.wearengine.notify.Notification;
import com.huawei.wearengine.notify.NotificationTemplate;
import com.huawei.wearengine.notify.NotificationConstants;
import java.util.HashMap;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;

final class WearableChannel {
    private final Context context;
    WearableChannel(Context context){this.context=context;}
    void prompt(String targetUuid,String pcLabel,PromptGate gate,PromptGate.Ticket ticket,BooleanSupplier enabled,Consumer<Boolean> clicked,Consumer<String> status){
        prompt(targetUuid,"是否解锁电脑？",pcLabel+" · 30 秒内有效",gate,ticket,enabled,clicked,status);
    }
    void prompt(String targetUuid,String title,String text,PromptGate gate,PromptGate.Ticket ticket,BooleanSupplier enabled,Consumer<Boolean> clicked,Consumer<String> status){
        prompt(targetUuid,title,text,gate,ticket,enabled,clicked,status,()->{});
    }
    void prompt(String targetUuid,String title,String text,PromptGate gate,PromptGate.Ticket ticket,BooleanSupplier enabled,Consumer<Boolean> clicked,Consumer<String> status,Runnable sdkAccepted){
        if(!enabled.getAsBoolean() || !gate.isActive(ticket))return;
        try {
        // getBondedDevices is the P2P list and can leave Notify capability unknown.
        // getCommonDevice supplies the capability information used by template notifications.
        HiWear.getDeviceClient(context).getCommonDevice().addOnSuccessListener(devices->{
            if(!enabled.getAsBoolean() || !gate.isActive(ticket))return;
            try {
            Device target=null;
            for(Device d:devices) if(targetUuid.equals(d.getUuid()))target=d;
            if(target==null || !target.isConnected()){gate.cancel(ticket);status.accept("目标手环未连接运动健康");return;}
            if(target.getNotifyCapability()!=Device.DEVICE_CAPABILITY_SUPPORT){gate.cancel(ticket);status.accept("手环未报告 Notify 能力："+target.getNotifyCapability());return;}
            HashMap<Integer,String> buttons=new HashMap<>();
            buttons.put(NotificationConstants.BUTTON_ONE_CONTENT_KEY,"一键解锁");
            buttons.put(NotificationConstants.BUTTON_TWO_CONTENT_KEY,"拒绝");
            Notification notification=new Notification.Builder().setTemplateId(NotificationTemplate.NOTIFICATION_TEMPLATE_TWO_BUTTONS)
                .setPackageName(context.getPackageName()).setTitle(title).setText(text)
                .setButtonContents(buttons).setAction(new Action(){
                    @Override public void onResult(Notification n,int feedback){
                        // 0=exit, 1=delete, 2=first button, 3=second button. Delivery success never approves.
                        if(!enabled.getAsBoolean())return;
                        if(feedback==2 || feedback==3){
                            if(gate.consume(ticket)){status.accept(feedback==2?"已收到本次有效确认，正在提交":"已收到本次有效拒绝，正在提交");clicked.accept(feedback==2);}
                            else status.accept("该条请求已过期或被替换，请等待新的请求");
                        }
                        else if(gate.isActive(ticket))status.accept("手环反馈："+feedback);
                    }
                    @Override public void onError(Notification n,int code,String message){if(gate.isActive(ticket)){gate.cancel(ticket);status.accept("Wear Engine Notify 错误："+code);}}
                }).build();
            final Device selected=target;
            gate.runIfActive(ticket,()->{
                if(!enabled.getAsBoolean())return;
                HiWear.getNotifyClient(context).notify(selected,notification).addOnSuccessListener(ignored->{if(gate.isActive(ticket)){sdkAccepted.run();status.accept("请求已发送，等待手环点击");}})
                    .addOnFailureListener(error->{if(gate.isActive(ticket)){gate.cancel(ticket);status.accept("Notify 调用失败："+WearErrors.describe(error));}});
            });
            }catch(RuntimeException error){fail(gate,ticket,status,error);}
        }).addOnFailureListener(error->{if(gate.isActive(ticket)){gate.cancel(ticket);status.accept("读取手环失败："+WearErrors.describe(error));}});
        }catch(RuntimeException error){fail(gate,ticket,status,error);}
    }
    private static void fail(PromptGate gate,PromptGate.Ticket ticket,Consumer<String> status,Exception error){
        if(gate.isActive(ticket)){gate.cancel(ticket);status.accept("手环调用失败："+WearErrors.describe(error));}
    }
}
