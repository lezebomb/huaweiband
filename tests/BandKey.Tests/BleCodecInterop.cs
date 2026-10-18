using System.Buffers.Binary;
using System.Text.Json;
using BandKey.Core;

internal static class BleCodecInterop
{
    internal static int Run(string mode,string directory)
    {
        Directory.CreateDirectory(directory);
        if(mode=="--ble-vectors"){
            using var output=File.Create(Path.Combine(directory,"csharp.bin"));var random=new Random(673214);Span<byte> size=stackalloc byte[2];
            for(int i=0;i<1000;i++){byte[] payload=new byte[i%160+1];random.NextBytes(payload);var bytes=new BlePipeFrame(3,0xfedcba9876540000UL+(uint)i,(uint)i,payload).Encode();BinaryPrimitives.WriteUInt16LittleEndian(size,(ushort)bytes.Length);output.Write(size);output.Write(bytes);}return 0;
        }
        byte[] original=File.ReadAllBytes(Path.Combine(directory,"csharp.bin")),roundtrip=File.ReadAllBytes(Path.Combine(directory,"java-roundtrip.bin"));
        if(!original.SequenceEqual(roundtrip))throw new Exception("Java frame roundtrip mismatch");
        using var input=File.OpenRead(Path.Combine(directory,"java.bin"));Span<byte> length=stackalloc byte[2];int passed=1;
        for(int i=0;i<1000;i++){
            input.ReadExactly(length);int size=BinaryPrimitives.ReadUInt16LittleEndian(length);if(size is <14 or >173)throw new Exception("Java frame size");
            byte[] bytes=new byte[size];input.ReadExactly(bytes);var frame=BlePipeFrame.Decode(bytes);
            if(frame.Kind!=3 || frame.Session!=0xfedcba9876540000UL+(uint)i || frame.Sequence!=(uint)i || !frame.Payload.All(b=>b==(byte)i) || frame.Payload.Length!=i%160+1)throw new Exception("Java frame mismatch");passed++;
        }
        if(input.ReadByte()!=-1)throw new Exception("Trailing Java frame bytes");
        File.WriteAllText(Path.Combine(directory,"interop.json"),JsonSerializer.Serialize(new{passed,failed=0,simulated=true,physicalBleTested=false}));Console.WriteLine($"BLE C#/Java interop: {passed} checks; 1000 frames each direction, no physical BLE.");return 0;
    }
}
