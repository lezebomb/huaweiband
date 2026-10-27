package dev.bandkey.bridge;

/** Protocol scope and local SDK cadence; neither creates an unlock approval. */
public final class HostConfirmationPolicy {
    public static boolean isHost(String protocol){return "bandkey-host-pilot-v1".equals(protocol);}
    public static boolean validConfiguration(String protocol,boolean testOnly,boolean realWearableOnly){
        return testOnly && ("bandkey-vm-phone-v1".equals(protocol) || isHost(protocol) && realWearableOnly);
    }
    public static boolean mayDecide(boolean host,boolean realWearableClient){return !host || realWearableClient;}
    public static long queryDelay(Boolean connected,boolean returned){
        return Boolean.FALSE.equals(connected) || returned?1000:3000;
    }
    private HostConfirmationPolicy(){}
}
