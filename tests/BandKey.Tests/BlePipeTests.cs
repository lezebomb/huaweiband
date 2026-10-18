using BandKey.Core;

internal static class BlePipeTests
{
    internal static void Run(Action<string,Action> test)
    {
        void Check(bool value){if(!value)throw new Exception("BLE codec assertion");}
        void Refuses(Action action){try{action();}catch(ArgumentException){return;}catch(IOException){return;}throw new Exception("BLE input was accepted");}
        test("BLE codec has explicit unsigned little endian golden vector",()=>Check(Convert.ToHexString(new BlePipeFrame(3,0x0102030405060708,2,[0xaa,0xbb]).Encode())=="03080706050403020102000000AABB"));
        test("BLE all control kinds and maximum data roundtrip",()=>{foreach(byte kind in new byte[]{1,2,3,4,5}){var frame=new BlePipeFrame(kind,ulong.MaxValue,65535,kind==3?new byte[160]:[]);var decoded=BlePipeFrame.Decode(frame.Encode());Check(decoded.Kind==kind && decoded.Session==frame.Session && decoded.Sequence==frame.Sequence && decoded.Payload.SequenceEqual(frame.Payload));}});
        test("BLE malformed frame sizes and unknown types rejected",()=>{foreach(int size in new[]{0,12,174})Refuses(()=>BlePipeFrame.Decode(new byte[size]));Refuses(()=>new BlePipeFrame(6,1,0,[]).Encode());});
        test("BLE zero session, sequence overflow and invalid payloads rejected",()=>{Refuses(()=>new BlePipeFrame(1,0,0,[]).Encode());Refuses(()=>new BlePipeFrame(1,1,65536,[]).Encode());Refuses(()=>new BlePipeFrame(3,1,0,[]).Encode());Refuses(()=>new BlePipeFrame(1,1,0,[1]).Encode());});
        test("BLE duplicate, reordered and foreign-session frames rejected",()=>{foreach(var frame in new[]{new BlePipeFrame(3,1,0,[1]),new BlePipeFrame(3,1,2,[1]),new BlePipeFrame(3,2,1,[1])}){var receiver=new BlePipeReceiver(1,1);receiver.Accept(new(1,1,0,[]));Refuses(()=>receiver.Accept(frame));}});
        test("BLE requires OPEN or READY before data",()=>{var receiver=new BlePipeReceiver(1,2);Refuses(()=>receiver.Accept(new(3,1,0,[1])));receiver.Accept(new(2,1,0,[]));Refuses(()=>receiver.Accept(new(1,1,1,[])));});
        test("BLE rejects every frame after EOF or ABORT",()=>{foreach(byte kind in new byte[]{4,5}){var receiver=new BlePipeReceiver(1,1);receiver.Accept(new(1,1,0,[]));receiver.Accept(new(kind,1,1,[]));Refuses(()=>receiver.Accept(new(3,1,2,[1])));}});
        test("BLE byte budget rejects excess without accepting more data",()=>{var receiver=new BlePipeReceiver(1,1);receiver.Accept(new(1,1,0,[]));uint seq=1;for(int i=0;i<819;i++)receiver.Accept(new(3,1,seq++,new byte[160]));receiver.Accept(new(3,1,seq++,new byte[32]));Refuses(()=>receiver.Accept(new(3,1,seq,[1])));});
        test("BLE 1000 deterministic random frame roundtrips",()=>{var random=new Random(81973);for(int i=0;i<1000;i++){byte[] data=new byte[random.Next(1,161)];random.NextBytes(data);var frame=new BlePipeFrame(3,(ulong)i+1,(uint)random.Next(65536),data);Check(BlePipeFrame.Decode(frame.Encode()).Encode().SequenceEqual(frame.Encode()));}});
    }
}
