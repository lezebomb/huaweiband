package dev.bandkey.aimakey;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import com.huawei.wearengine.HiWear;
import com.huawei.wearengine.device.Device;
import com.huawei.wearengine.p2p.*;
import java.util.function.Consumer;

/** SDK-compiled transport seam. Not enabled by the simulator: no installable peer is yet verified. */
final class WearPeerTransport {
    private final Context context;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private Receiver receiver;
    private Peer peer;
    private long generation;
    WearPeerTransport(Context context) { this.context = context.getApplicationContext(); }
    void connectVerifiedPeer(Device device, String packageName, String sha256, boolean installationVerified,
                             Consumer<byte[]> received, Consumer<String> status) {
        close();
        if (!installationVerified || device == null || !device.isConnected() || device.getP2pCapability() != Device.DEVICE_CAPABILITY_SUPPORT
            || !packageName.matches("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+") || !sha256.matches("[A-Fa-f0-9]{64}")) {
            status.accept("PEER_NOT_VERIFIED"); return;
        }
        long ticket = generation;
        Peer selected = new Peer.Builder().setDevice(device).setPkgName(packageName).setFingerPrint(sha256).build();
        Receiver selectedReceiver = message -> {
            byte[] data = message.getData();
            if (data == null || data.length > 256 || message.getType() != Message.MESSAGE_TYPE_DATA) return;
            byte[] copy = data.clone();
            handler.post(() -> { if (ticket == generation && peer == selected) received.accept(copy); });
        };
        receiver = selectedReceiver;
        try {
            HiWear.getP2pClient(context).isAppInstalled(device, packageName).addOnSuccessListener(installed -> {
                if (ticket != generation) return;
                if (!installed) { status.accept("PEER_NOT_INSTALLED"); return; }
                HiWear.getP2pClient(context).registerReceiver(selected, selectedReceiver)
                    .addOnSuccessListener(ignored -> { if (ticket == generation) { peer = selected; status.accept("READY"); } else closeReceiver(selectedReceiver); })
                    .addOnFailureListener(error -> { if (ticket == generation) status.accept("REGISTER_FAILED"); });
            }).addOnFailureListener(error -> { if (ticket == generation) status.accept("PEER_QUERY_FAILED"); });
        } catch (RuntimeException e) { status.accept("SDK_UNAVAILABLE"); }
    }
    void send(byte[] bytes, Consumer<String> delivery) {
        Peer selected = peer; long ticket = generation;
        if (selected == null || bytes == null || bytes.length > 1024) { delivery.accept("NOT_READY"); return; }
        try {
            Message message = new Message.Builder().setPayload(bytes.clone()).build();
            HiWear.getP2pClient(context).send(selected, message, new SendCallback() {
                public void onSendProgress(long progress) {}
                public void onSendResult(int code) {
                    handler.post(() -> { if (ticket == generation) delivery.accept("SDK_DELIVERY_CODE_" + code); });
                }
            }).addOnFailureListener(error -> { if (ticket == generation) delivery.accept("SDK_SEND_FAILED"); });
        } catch (RuntimeException e) { delivery.accept("SDK_SEND_FAILED"); }
        // Delivery is never treated as vehicle acknowledgement. Commands are never retried here.
    }
    void close() {
        generation++; peer = null;
        if (receiver != null) closeReceiver(receiver);
        receiver = null;
    }
    private void closeReceiver(Receiver value) {
        try { HiWear.getP2pClient(context).unregisterReceiver(value); } catch (RuntimeException ignored) {}
    }
}
