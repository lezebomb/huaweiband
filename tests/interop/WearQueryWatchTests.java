package dev.bandkey.bridge;

public final class WearQueryWatchTests {
    static void check(boolean value){if(!value)throw new AssertionError("Query recovery");}
    public static void main(String[] args)throws Exception{
        WearQueryWatch watch=new WearQueryWatch();long first=watch.begin(100);
        check(!watch.expire(first,5099) && watch.expire(first,5100));
        long second=watch.begin(6100);check(!watch.complete(first,6101) && watch.complete(second,6102));
        check(!watch.expire(second,11100) && !watch.complete(second,6103));
        long late=watch.begin(12000);check(!watch.complete(late,17000) && watch.expire(late,17000));
        long stopped=watch.begin(18000);watch.cancel();check(!watch.complete(stopped,18001) && !watch.expire(stopped,23000));
        long rolled=watch.begin(24000);check(!watch.complete(rolled,23999) && watch.expire(rolled,23999));
        java.nio.file.Files.writeString(java.nio.file.Path.of(args[0]),"{\"passed\":6,\"failed\":0,\"realWearableTested\":false}");
        System.out.println("Wear query timeout / stale callback: 6/6 passed");
    }
}
