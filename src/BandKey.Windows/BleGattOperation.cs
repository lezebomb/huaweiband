namespace BandKey.Windows;

// A closed WinRT GATT object invalidates this generation, never the TLS or
// authorization rules. No application decision is replayed by this helper.
internal static class BleGattOperation
{
    internal static async Task<T> WriteAsync<T>(Func<CancellationToken,Task<T>> write,CancellationToken cancellation,TimeSpan? timeout=null)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(timeout??TimeSpan.FromSeconds(5));
        try{
            cancellation.ThrowIfCancellationRequested();
            return await write(budget.Token).WaitAsync(budget.Token);
        }
        catch(ObjectDisposedException error){
            if(cancellation.IsCancellationRequested)throw new OperationCanceledException("GATT write cancelled",error,cancellation);
            throw new BleTransportException("gatt-session-closed",error);
        }
        catch(OperationCanceledException error)when(!cancellation.IsCancellationRequested){throw new BleTransportException("gatt-write-timeout",error);}
    }
}
