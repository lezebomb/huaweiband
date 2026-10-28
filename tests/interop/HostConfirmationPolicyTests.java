package dev.bandkey.bridge;
import java.nio.file.*;
public final class HostConfirmationPolicyTests {
    private static void check(boolean value){if(!value)throw new AssertionError("Host confirmation policy");}
    public static void main(String[] args)throws Exception{
        check(HostConfirmationPolicy.validConfiguration("bandkey-vm-phone-v1",true,false));
        check(HostConfirmationPolicy.validConfiguration("bandkey-host-pilot-v1",true,true) && !HostConfirmationPolicy.validConfiguration("bandkey-host-pilot-v1",true,false) && !HostConfirmationPolicy.validConfiguration("other",true,true) && !HostConfirmationPolicy.validConfiguration("bandkey-host-pilot-v1",false,true));
        check(!HostConfirmationPolicy.mayDecide(true,false) && HostConfirmationPolicy.mayDecide(true,true) && HostConfirmationPolicy.mayDecide(false,false));
        check(HostConfirmationPolicy.queryDelay(false,false)==1000 && HostConfirmationPolicy.queryDelay(true,true)==1000 && HostConfirmationPolicy.queryDelay(true,false)==3000 && HostConfirmationPolicy.queryDelay(null,false)==1000);
        if(args.length==1)Files.writeString(Path.of(args[0]),"{\"total\":4,\"passed\":4,\"failed\":0,\"hardwareTested\":false}");
        System.out.println("Host real-band scope and local SDK cadence: 4/4 passed.");
    }
}
