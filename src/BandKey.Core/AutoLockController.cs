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
    private BandConnectionMonitor bandConnection;
    public ProximityInputMode InputMode { get; private set; } = ProximityInputMode.DirectBandAdvertisement;
    public bool CanRequestConfirmation => Locked == true && Presence.State == PresenceState.Near;
    public bool Enabled { get; set; }
    public bool? Locked { get; private set; }
    public PresenceSnapshot Presence { get; private set; } = new(PresenceState.Unknown, null, false, false, "startup");
    public event Action<LockRequestResult>? LockAttempted;
    public AutoLockController(PresenceOptions options, IInteractiveSession session)
    { monitor = new(options); bandConnection = new(options); this.session = session; }
    public void SetSession(bool? locked)
    {
        if (Locked != locked) Reset("session-change");
        Locked = locked;
    }
    public void Reset(string reason)
    { monitor.Reset(); bandConnection.Reset(); Presence = new(PresenceState.Unknown, null, false, false, reason); }
    public void Reconfigure(PresenceOptions options)
    { monitor = new(options); bandConnection = new(options); Presence = new(PresenceState.Unknown, null, false, false, "settings-changed"); }
    public void Observe(int rssi, double now, bool healthy)
    { RequireMode(ProximityInputMode.DirectBandAdvertisement); Presence = monitor.Observe(rssi, now, healthy); Apply(); }
    public void Tick(double now, bool healthy)
    { Presence = InputMode == ProximityInputMode.FixedPhoneBandConnection ? bandConnection.Tick(now, healthy) : monitor.Tick(now, healthy); Apply(); }
    public void SelectInputMode(ProximityInputMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown proximity mode");
        if (InputMode == mode) return;
        InputMode = mode; Reset("proximity-mode-changed");
    }
    // The caller must authenticate the phone report and enforce heartbeat freshness before this call.
    public void ObserveBandConnection(bool? connected, double now, bool authenticatedSourceHealthy)
    {
        RequireMode(ProximityInputMode.FixedPhoneBandConnection);
        Presence = bandConnection.Observe(connected, now, authenticatedSourceHealthy); Apply();
    }
    // RSSI comes from the selected phone's PC BLE link, not a neighbouring advertiser.
    public void ObserveCarriedPhone(int rssi, double now, bool boundBleSourceHealthy)
    {
        RequireMode(ProximityInputMode.CarriedPhoneRssi);
        Presence = monitor.Observe(rssi, now, boundBleSourceHealthy); Apply();
    }
    private void RequireMode(ProximityInputMode expected)
    { if (InputMode != expected) throw new InvalidOperationException("Proximity input does not match selected mode"); }
    private void Apply()
    {
        if (!Enabled || !Presence.ShouldLock || Locked != false) return;
        // A service in Session 0 must never attempt interactive desktop locking.
        var result = session.IsInteractive ? session.RequestLock() : new LockRequestResult(false, "non-interactive-session");
        LockAttempted?.Invoke(result);
    }
}
