import android.content.Context;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.auth.AuthCallback;
import com.huawei.wearengine.auth.Permission;
import com.huawei.wearengine.device.Device;
import com.huawei.wearengine.notify.Action;
import com.huawei.wearengine.notify.Notification;
import com.huawei.wearengine.notify.NotificationConstants;
import com.huawei.wearengine.notify.NotificationTemplate;
import java.util.HashMap;

/** Compile-only check; no main entry point, device access or authorization attempt. */
final class WearEngineArchiveSmoke {
    static void permissions(Context context, AuthCallback callback) {
        HiWear.getAuthClient(context).requestPermission(callback, Permission.DEVICE_MANAGER, Permission.NOTIFY);
    }
    static void selectedDevice(Context context) {
        HiWear.getDeviceClient(context).getCommonDevice().addOnSuccessListener(devices -> {
            for (Device device : devices) {
                if (device.isConnected() && device.getNotifyCapability() == Device.DEVICE_CAPABILITY_SUPPORT)
                    device.getUuid();
            }
        });
    }
    static void notification(Context context, Device device, Action callback) {
        HashMap<Integer,String> buttons = new HashMap<>();
        buttons.put(NotificationConstants.BUTTON_ONE_CONTENT_KEY, "确认");
        buttons.put(NotificationConstants.BUTTON_TWO_CONTENT_KEY, "拒绝");
        Notification value = new Notification.Builder().setTemplateId(NotificationTemplate.NOTIFICATION_TEMPLATE_TWO_BUTTONS)
            .setPackageName(context.getPackageName()).setTitle("SDK 接口编译检查").setText("仅编译，不执行")
            .setButtonContents(buttons).setAction(callback).build();
        HiWear.getNotifyClient(context).notify(device, value);
    }
}