using BandKey.Core;

namespace BandKey.SessionLab;

internal enum ObservationSource { Query, Notification }
internal sealed record SessionEvent(double ElapsedMilliseconds,ObservationSource Source,bool? Locked);
internal sealed record LockAttempt(double ElapsedMilliseconds,bool Submitted,string Code);

// Notification and query sequences are independent: a lagging query cannot turn
// a lock notification into a false unlock observation.
internal sealed class SessionEvidence(bool simulatedEnvironment)
{
    private readonly List<SessionEvent> events=[];
    private readonly List<LockAttempt> attempts=[];
    private readonly bool?[] last=new bool?[2];
    private readonly bool[] initialized=new bool[2];
    private double previous=-1;
    internal bool Truncated{get;private set;}
    internal int NotificationUnlocksAfterLock{get;private set;}
    internal int QueryUnlocksAfterLock{get;private set;}
    internal bool SimulatedEnvironment=>simulatedEnvironment;
    internal SessionEvent[] Events=>events.ToArray();
    internal LockAttempt[] Attempts=>attempts.ToArray();
    private void Time(double elapsed)
    {
        if(!double.IsFinite(elapsed) || elapsed<0 || elapsed<previous)throw new ArgumentException("Monotonic elapsed time required");
        previous=elapsed;
    }
    internal void Observe(bool? locked,ObservationSource source,double elapsed)
    {
        if(!Enum.IsDefined(source))throw new ArgumentException("Unknown observation source");
        Time(elapsed);int index=(int)source;
        if(Truncated || initialized[index] && last[index]==locked)return;
        if(events.Count>=512){Truncated=true;return;}
        if(initialized[index] && last[index]==true && locked==false)
        {
            if(source==ObservationSource.Query)QueryUnlocksAfterLock++;else NotificationUnlocksAfterLock++;
        }
        last[index]=locked;initialized[index]=true;events.Add(new(elapsed,source,locked));
    }
    internal void Request(LockRequestResult result,double elapsed)
    {
        Time(elapsed);
        if(attempts.Count>=16){Truncated=true;return;}
        string code=result.Code is "submitted" or "non-interactive-session" or "guest-required" or "state-unknown-or-locked" or "preview-disabled"?result.Code:
            System.Text.RegularExpressions.Regex.IsMatch(result.Code,@"^win32-[0-9]{1,10}$")?result.Code:"request-failed";
        attempts.Add(new(elapsed,result.Submitted,code));
    }
    internal object Report()=>new
    {
        schema="bandkey-vm-session-lab-v1",generatedAt=DateTimeOffset.UtcNow,simulatedEnvironment,
        actualWindowsObservation=!simulatedEnvironment && events.Any(e=>e.Locked!=null),Truncated,
        NotificationUnlocksAfterLock,QueryUnlocksAfterLock,
        observedUnlockAfterObservedLock=NotificationUnlocksAfterLock>0 || QueryUnlocksAfterLock>0,
        unlockAttributedToBandKey=false,windowsAuthenticationSubmittedByThisTool=false,credentialsAccessed=false,realWearableTested=false,
        events=Events,lockAttempts=Attempts
    };
}
