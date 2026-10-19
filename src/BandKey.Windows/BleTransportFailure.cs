using System.Net.Sockets;

namespace BandKey.Windows;

// Codes are supplied by this assembly, never by a peer or exception message.
public sealed record BleStreamProgress(bool PhoneEof,bool TargetEof,long SentBytes,long ReceivedBytes);

public sealed class BleTransportException(string code,Exception? inner=null,BleStreamProgress? progress=null) : IOException(code,inner)
{
    public string Code { get; }=code;
    public BleStreamProgress? Progress{get;}=progress;
}

public sealed record BleTransportFailure(string Code,string ExceptionType,int HResult,int? SocketError,BleStreamProgress? Stream=null)
{
    public static BleTransportFailure From(Exception error,string fallback="transport-failed")
    {
        string code=error is BleTransportException transport?transport.Code:fallback;
        var progress=(error as BleTransportException)?.Progress;
        Exception root=error;
        for(int depth=0;depth<8 && root.InnerException!=null;depth++)
        {
            root=root.InnerException;
            if(root is BleTransportException nested){code=nested.Code;progress??=nested.Progress;}
        }
        return new(code,root.GetType().Name,root.HResult,root is SocketException socket?(int)socket.SocketErrorCode:null,progress);
    }
}
