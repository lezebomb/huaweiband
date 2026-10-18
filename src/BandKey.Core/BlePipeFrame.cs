using System.Buffers.Binary;

namespace BandKey.Core;

/// <summary>Transport framing only. The enclosed stream still requires pinned TLS.</summary>
public sealed record BlePipeFrame(byte Kind,ulong Session,uint Sequence,byte[] Payload)
{
    public const byte Open=1,Ready=2,Data=3,Eof=4,Abort=5;
    public const int Header=13,MaxPayload=160,MaxBytes=131072;
    public byte[] Encode()
    {
        Validate();var bytes=new byte[Header+Payload.Length];bytes[0]=Kind;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1),Session);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9),Sequence);Payload.CopyTo(bytes,Header);return bytes;
    }
    private void Validate()
    {
        if(Kind is <Open or >Abort || Session==0 || Sequence>=65536 || Payload.Length>MaxPayload || (Kind==Data?Payload.Length==0:Payload.Length!=0))throw new ArgumentException("Invalid BLE pipe frame");
    }
    public static BlePipeFrame Decode(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length<Header || bytes.Length>Header+MaxPayload)throw new ArgumentException("BLE frame length");
        var value=new BlePipeFrame(bytes[0],BinaryPrimitives.ReadUInt64LittleEndian(bytes[1..]),BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]),bytes[Header..].ToArray());value.Validate();return value;
    }
}

public sealed class BlePipeReceiver(ulong session,byte initialKind)
{
    private uint next;private int bytes;private bool ended;
    public void Accept(BlePipeFrame frame)
    {
        // Encode validates objects supplied directly as well as decoded frames.
        _=frame.Encode();
        if(ended || frame.Session!=session || frame.Sequence!=next || (next==0?frame.Kind!=initialKind:frame.Kind is not (BlePipeFrame.Data or BlePipeFrame.Eof or BlePipeFrame.Abort)))throw new IOException("BLE session, sequence or lifecycle mismatch");
        if(bytes+frame.Payload.Length>BlePipeFrame.MaxBytes)throw new IOException("BLE stream limit");
        bytes+=frame.Payload.Length;next++;ended=frame.Kind is BlePipeFrame.Eof or BlePipeFrame.Abort;
    }
}
