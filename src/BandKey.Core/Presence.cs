namespace BandKey.Core;

public enum PresenceState { Unknown, Near, Uncertain, Far }

public sealed record PresenceOptions
{
    public int NearRssi { get; init; } = -65;
    public int FarRssi { get; init; } = -80;
    public int MedianWindow { get; init; } = 5;
    public double Alpha { get; init; } = 0.35;
    public double NearSeconds { get; init; } = 3;
    public double FarSeconds { get; init; } = 15;
    public double StaleSeconds { get; init; } = 8;
    public double MissingSeconds { get; init; } = 45;
    public bool LockOnMissingSignal { get; init; }

    public void Validate()
    {
        if (NearRssi > -10 || FarRssi < -120 || NearRssi - FarRssi < 5 ||
            MedianWindow is < 3 or > 31 || MedianWindow % 2 == 0 ||
            !double.IsFinite(Alpha) || Alpha is <= 0 or > 1 ||
            !double.IsFinite(NearSeconds) || NearSeconds is < 1 or > 60 ||
            !double.IsFinite(FarSeconds) || FarSeconds is < 3 or > 600 ||
            !double.IsFinite(StaleSeconds) || StaleSeconds is < 2 or > 120 ||
            !double.IsFinite(MissingSeconds) || MissingSeconds < StaleSeconds + FarSeconds || MissingSeconds > 1800)
            throw new ArgumentException("Invalid proximity settings / 参数范围或滞回设置无效。");
    }
}

public sealed record PresenceSnapshot(PresenceState State, double? FilteredRssi, bool Armed,
    bool ShouldLock, string Reason);

/// <summary>All times are monotonic seconds, never wall-clock time. One instance per BLE target.</summary>
public sealed class PresenceMonitor
{
    private readonly PresenceOptions options;
    private readonly Queue<int> samples = new();
    private double lastSample = double.NegativeInfinity, lastNow = double.NegativeInfinity;
    private double? nearSince, farSince, filtered;
    private bool armed, lockIssued;
    private PresenceState state;

    public PresenceMonitor(PresenceOptions options) { options.Validate(); this.options = options; }
    public void Reset()
    {
        samples.Clear(); nearSince = farSince = filtered = null;
        lastSample = double.NegativeInfinity; armed = lockIssued = false; state = PresenceState.Unknown;
    }

    public PresenceSnapshot Observe(int rssi, double now, bool scannerHealthy = true)
    {
        CheckTime(now);
        if (!scannerHealthy) { Reset(); return Snapshot(false, "scanner-unavailable"); }
        if (rssi is < -120 or > -10) return Snapshot(false, "invalid-rssi");
        if (now - lastSample >= options.StaleSeconds)
        {
            samples.Clear(); filtered = null; nearSince = farSince = null;
            if (!options.LockOnMissingSignal) armed = lockIssued = false;
        }
        lastSample = now; samples.Enqueue(rssi);
        if (samples.Count > options.MedianWindow) samples.Dequeue();
        var sorted = samples.Order().ToArray();
        var median = sorted[sorted.Length / 2];
        filtered = filtered is null ? median : options.Alpha * median + (1 - options.Alpha) * filtered;
        if (samples.Count < 3) { state = PresenceState.Unknown; return Snapshot(false, "warming-up"); }
        if (filtered >= options.NearRssi)
        {
            farSince = null; nearSince ??= now;
            if (now - nearSince >= options.NearSeconds)
            { state = PresenceState.Near; armed = true; lockIssued = false; }
            else state = PresenceState.Uncertain;
        }
        else if (filtered <= options.FarRssi)
        {
            nearSince = null; farSince ??= now;
            state = now - farSince >= options.FarSeconds ? PresenceState.Far : PresenceState.Uncertain;
        }
        else { nearSince = farSince = null; state = PresenceState.Uncertain; }
        return EmitIfFar("rssi");
    }

    public PresenceSnapshot Tick(double now, bool scannerHealthy)
    {
        CheckTime(now);
        if (!scannerHealthy) { Reset(); return Snapshot(false, "scanner-unavailable"); }
        if (now - lastSample >= options.StaleSeconds)
        {
            nearSince = farSince = null;
            if (!options.LockOnMissingSignal) armed = lockIssued = false;
            state = armed && options.LockOnMissingSignal && now - lastSample >= options.MissingSeconds
                ? PresenceState.Far : PresenceState.Unknown;
            return EmitIfFar("signal-missing");
        }
        // Dwell times only progress on actual observations; silence is not a weak RSSI sample.
        return Snapshot(false, "waiting");
    }

    private PresenceSnapshot EmitIfFar(string reason)
    {
        bool shouldLock = armed && !lockIssued && state == PresenceState.Far;
        if (shouldLock) lockIssued = true;
        return Snapshot(shouldLock, reason);
    }
    private PresenceSnapshot Snapshot(bool shouldLock, string reason) => new(state, filtered, armed, shouldLock, reason);
    private void CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < lastNow) throw new ArgumentException("Monotonic time required");
        lastNow = now;
    }
}
