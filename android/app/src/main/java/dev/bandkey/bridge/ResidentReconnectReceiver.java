package dev.bandkey.bridge;

import android.bluetooth.BluetoothDevice;
import android.content.*;

/** Protected system connection event is a recovery hint, never distance or approval.
 * No device address/name is read or retained. Explicit user stop still wins. */
public final class ResidentReconnectReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent){
        if(intent!=null && BluetoothDevice.ACTION_ACL_CONNECTED.equals(intent.getAction()))
            ResidentRecoveryJob.recover(context,"bluetooth-connected");
    }
}
