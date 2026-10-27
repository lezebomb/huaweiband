package dev.bandkey.bridge;
public final class HostResidentTests {
    private static void check(boolean value){if(!value)throw new AssertionError("Resident lifetime");}
    private static BleBackgroundLease.Factory radio(){return ()->new BleBackgroundLease.Transport(){public void start(){}public void close(){}public BleBackgroundLease.Stats stats(){return new BleBackgroundLease.Stats(true,true,false,0,0,0);}};}
    private static VmWearSession<Object> session(long[] time,boolean resident){return new VmWearSession<>(()->time[0]/1000,()->time[0],1000,()->true,()->false,new VmWearSession.Client<>(){public VmWearSession.Request<Object> poll(){throw new AssertionError("No poll expected");}public VmWearSession.Result decide(Object p,boolean yes){throw new AssertionError("No decision expected");}},(g,t,a,c)->{throw new AssertionError("No prompt expected");},Runnable::run,resident);}
    public static void main(String[] args)throws Exception{
        BleBackgroundLease vm=new BleBackgroundLease();check(vm.start(0,1000,radio()));vm.tick(1001,true);check(!vm.allows(1001));
        BleBackgroundLease host=new BleBackgroundLease(true);check(host.start(0,1000,radio()));host.tick(3600000,true);check(host.allows(3600000));
        host.stop(BleBackgroundLease.Reason.USER);check(!host.allows(3600000));
        long[] t={0};VmWearSession<Object> old=session(t,false),current=session(t,true);t[0]=3600000;check(!old.active() && current.active());current.stop();check(!current.active());
        BleBackgroundLease rolled=new BleBackgroundLease(true);rolled.start(100,1000,radio());rolled.tick(99,true);check(!rolled.allows(100));
        if(args.length==1)java.nio.file.Files.writeString(java.nio.file.Path.of(args[0]),"{\"total\":5,\"passed\":5,\"failed\":0,\"hardwareTested\":false}");
        System.out.println("Host resident lifetime, stop and clock rollback: 5/5 passed.");
    }
}
