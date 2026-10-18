using BandKey.Core;

internal static class SafetyTests
{
    private sealed class Session : IInteractiveSession
    {
        public bool IsInteractive { get; set; } = true;
        public bool? IsLocked() => false;
        public int Requests;
        public bool Submitted = true;
        public LockRequestResult RequestLock() { Requests++; return new(Submitted, Submitted ? "submitted" : "failed"); }
    }
    private static void Assert(bool ok) { if (!ok) throw new Exception("Unexpected lock policy outcome"); }
    private static AutoLockController Controller(Session session, bool enabled = true)
    {
        var controller = new AutoLockController(new PresenceOptions { MedianWindow = 3, Alpha = 1, NearSeconds = 1, FarSeconds = 3, StaleSeconds = 5, MissingSeconds = 12 }, session) { Enabled = enabled };
        controller.SetSession(false); return controller;
    }
    private static void Near(AutoLockController c, int start = 0) { for (int i = start; i < start + 8; i++) c.Observe(-50, i, true); }
    private static void Far(AutoLockController c, int start = 8) { for (int i = start; i < start + 20; i++) c.Observe(-95, i, true); }
    public static void Run(Action<string, Action> test)
    {
        test("actual lock policy submits once and waits for WTS confirmation", () =>
        { var s = new Session(); var c = Controller(s); Near(c); Far(c); Assert(s.Requests == 1 && c.Locked == false); c.SetSession(true); Assert(c.Locked == true && !c.Presence.Armed); });
        test("disabled auto-lock never calls session API", () =>
        { var s = new Session(); var c = Controller(s, false); Near(c); Far(c); Assert(s.Requests == 0); });
        test("Session 0 cannot call interactive lock API", () =>
        { var s = new Session { IsInteractive = false }; var c = Controller(s); Near(c); Far(c); Assert(s.Requests == 0); });
        test("unknown session cannot call lock API", () =>
        { var s = new Session(); var c = Controller(s); c.SetSession(null); Near(c); Far(c); Assert(s.Requests == 0); });
        test("already locked session cannot submit another lock", () =>
        { var s = new Session(); var c = Controller(s); c.SetSession(true); Near(c); Far(c); Assert(s.Requests == 0); });
        test("failed lock request does not loop on every packet", () =>
        { var s = new Session { Submitted = false }; var c = Controller(s); Near(c); Far(c); Assert(s.Requests == 1 && c.Locked == false); });
        test("manual unlock requires fresh stable near before departure", () =>
        { var s = new Session(); var c = Controller(s); Near(c); c.SetSession(true); c.SetSession(false); Far(c); Assert(s.Requests == 0); Near(c, 30); Far(c, 38); Assert(s.Requests == 1); });
        test("scanner failure cancels arming before weak-signal reconnection", () =>
        { var s = new Session(); var c = Controller(s); Near(c); c.Tick(8, false); Far(c, 9); Assert(s.Requests == 0); });
        test("sleep resume cancels arming until stable near", () =>
        { var s = new Session(); var c = Controller(s); Near(c); c.Reset("resume"); Far(c); Assert(s.Requests == 0); });
        test("default stale signal disarms rather than locking on reconnection", () =>
        { var s = new Session(); var c = Controller(s); Near(c); c.Tick(20, true); Far(c, 21); Assert(s.Requests == 0 && !c.Presence.Armed); });
        test("long gap noticed by observation also requires fresh near", () =>
        { var s = new Session(); var c = Controller(s); Near(c); Far(c, 30); Assert(s.Requests == 0); });
        test("short interruption does not create a false lock", () =>
        { var s = new Session(); var c = Controller(s); Near(c); c.Tick(9, true); c.Observe(-110, 10, true); c.Observe(-50, 11, true); c.Observe(-50, 12, true); Assert(s.Requests == 0); });
        test("same address with different address type is not selected target", () =>
        { var target = new ObservationTarget("112233445566", "Public"); Assert(!target.Matches(new("112233445566", "same name", -50, DateTimeOffset.UtcNow, "Random", []))); });
        test("random address rotation is never guessed from matching name", () =>
        { var target = new ObservationTarget("112233445566", "Random"); Assert(!target.Matches(new("112233445577", "HUAWEI Band 11 Pro", -50, DateTimeOffset.UtcNow, "Random", []))); });
        test("anonymous advertisement cannot identify a target", () =>
        { var target = new ObservationTarget("000000000000", null); Assert(!target.Matches(new("000000000000", "", -50, DateTimeOffset.UtcNow, "Unspecified", [], Anonymous: true))); });
    }
}
