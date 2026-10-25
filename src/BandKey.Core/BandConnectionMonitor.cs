namespace BandKey.Core;

public enum ProximityInputMode { DirectBandAdvertisement, FixedPhoneBandConnection, CarriedPhoneRssi }

/// <summary>Connection-range evidence from a phone kept at the computer. This is not meter ranging.</summary>
public sealed class BandConnectionMonitor
{
    private readonly PresenceOptions options;
    private double lastNow = double.NegativeInfinity, lastSample = double.NegativeInfinity;
    private double? connectedSince, disconnectedSince;
    private bool armed, issued;
    private PresenceState state = PresenceState.Unknown;
    public BandConnectionMonitor(PresenceOptions options) { options.Validate(); this.options = options; }
    public void Reset()
    {
        connectedSince = disconnectedSince = null; lastSample = double.NegativeInfinity;
        armed = issued = false; state = PresenceState.Unknown;
    }
    private void Time(double now)
    {
        if (!double.IsFinite(now) || now < lastNow) throw new ArgumentException("Monotonic time required");
        lastNow = now;
    }
    private PresenceSnapshot Snapshot(bool shouldLock, string reason) => new(state, null, armed, shouldLock, reason);
    public PresenceSnapshot Observe(bool? connected, double now, bool sourceHealthy)
    {
        Time(now);
        if (!sourceHealthy || connected == null) { Reset(); return Snapshot(false, "band-connection-unknown"); }
        if (now - lastSample >= options.StaleSeconds) Reset();
        lastSample = now;
        if (connected.Value)
        {
            disconnectedSince = null; connectedSince ??= now;
            if (now - connectedSince.Value >= options.NearSeconds) { state = PresenceState.Near; armed = true; issued = false; }
            else state = PresenceState.Uncertain;
        }
        else
        {
            connectedSince = null; disconnectedSince ??= now;
            state = now - disconnectedSince.Value >= options.FarSeconds ? PresenceState.Far : PresenceState.Uncertain;
        }
        bool shouldLock = armed && !issued && state == PresenceState.Far;
        if (shouldLock) issued = true;
        return Snapshot(shouldLock, connected.Value ? "band-connected-to-anchor" : "band-disconnected-from-anchor");
    }
    public PresenceSnapshot Tick(double now, bool sourceHealthy)
    {
        Time(now);
        if (!sourceHealthy || now - lastSample >= options.StaleSeconds)
        { Reset(); return Snapshot(false, "anchor-evidence-unavailable"); }
        return Snapshot(false, "waiting-anchor-evidence");
    }
}
