namespace BandKey.Windows;

// A missed OS disconnect callback must not leave the OPEN reader waiting forever.
internal static class BleIdleWatchdog
{
    internal static async Task RunAsync(Func<bool> idle,Func<CancellationToken,Task> probe,CancellationToken cancellation,
        TimeSpan? interval=null,TimeSpan? timeout=null)
    {
        while(true)
        {
            await Task.Delay(interval??TimeSpan.FromSeconds(5),cancellation);
            if(!idle())continue;
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(timeout??TimeSpan.FromSeconds(5));
            try{await probe(budget.Token).WaitAsync(budget.Token);}
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
            catch(OperationCanceledException error){throw new BleTransportException("gatt-health-timeout",error);}
            catch(BleTransportException){throw;}
            catch(UnauthorizedAccessException error){throw new BleTransportException("gatt-permission-denied",error);}
            catch(Exception error){throw new BleTransportException("gatt-health-failed",error);}
        }
    }
}
