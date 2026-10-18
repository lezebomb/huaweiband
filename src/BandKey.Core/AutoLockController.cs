namespace BandKey.Core;

public sealed record LockRequestResult(bool Submitted, string Code);
public interface IInteractiveSession
{
    bool IsInteractive { get; }
    bool? IsLocked();
    LockRequestResult RequestLock();
}

/// <summary>Runs in the user agent. Only session notifications/query confirm a lock.</summary>
public sealed class AutoLockController
{
    private readonly IInteractiveSession session;
    private PresenceMonitor monitor;
    public bool Enabled { get; set; }
    public bool? Locked { get; private set; }
    public PresenceSnapshot Presence { get; private set; } = new(PresenceState.Unknown, null, false, false, "startup");
    public event Action<LockRequestResult>? LockAttempted;
    public AutoLockController(PresenceOptions options, IInteractiveSession session)
    { monitor = new(options); this.session = session; }
    public void SetSession(bool? locked)
    {
        if (Locked != locked) Reset("session-change");
        Locked = locked;
    }
    public void Reset(string reason)
    { monitor.Reset(); Presence = new(PresenceState.Unknown, null, false, false, reason); }
    public void Reconfigure(PresenceOptions options)
    { monitor = new(options); Presence = new(PresenceState.Unknown, null, false, false, "settings-changed"); }
    public void Observe(int rssi, double now, bool healthy)
    { Presence = monitor.Observe(rssi, now, healthy); Apply(); }
    public void Tick(double now, bool healthy)
    { Presence = monitor.Tick(now, healthy); Apply(); }
    private void Apply()
    {
        if (!Enabled || !Presence.ShouldLock || Locked != false) return;
        // A service in Session 0 must never attempt interactive desktop locking.
        var result = session.IsInteractive ? session.RequestLock() : new LockRequestResult(false, "non-interactive-session");
        LockAttempted?.Invoke(result);
    }
}
