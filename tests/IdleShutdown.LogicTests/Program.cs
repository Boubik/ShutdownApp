using IdleShutdown.ServiceApp;
using IdleShutdown.AgentApp;
using IdleShutdown.Shared;

var tests = new (string Name, Action Run)[]
{
    ("interactive warning starts at the first configured boundary", () =>
    {
        Expect(false, IdleThreshold.IsReached(TimeSpan.FromSeconds(59), 1));
        Expect(true, IdleThreshold.IsReached(TimeSpan.FromSeconds(60), 1));
        Expect(true, IdleThreshold.IsReached(TimeSpan.FromSeconds(61), 1));
    }),

    ("no-user timeout fires exactly once at the configured boundary", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);
        var input = Utc(-10);

        Expect(
            NoUserObservation.Started,
            tracker.ObserveNoUser(start, Minutes(1), 1, input));

        Expect(
            NoUserObservation.Waiting,
            tracker.ObserveNoUser(start.AddSeconds(59), Minutes(1), 1, input));

        Expect(
            NoUserObservation.TimeoutReached,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, input));
    }),

    ("sign-in-screen input resets the complete no-user timeout", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);
        var firstInput = Utc(-10);
        var newInput = Utc(59);

        tracker.ObserveNoUser(start, Minutes(1), 1, firstInput);

        Expect(
            NoUserObservation.InputDetected,
            tracker.ObserveNoUser(start.AddSeconds(59), Minutes(1), 1, newInput));

        Expect(
            NoUserObservation.Waiting,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, newInput));

        Expect(
            NoUserObservation.TimeoutReached,
            tracker.ObserveNoUser(start.AddSeconds(119), Minutes(1), 1, newInput));
    }),

    ("input during the final shutdown check cancels the action", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);
        var firstInput = Utc(-10);

        tracker.ObserveNoUser(start, Minutes(1), 1, firstInput);

        Expect(
            NoUserObservation.TimeoutReached,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, firstInput));

        Expect(
            NoUserObservation.InputDetected,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, Utc(60)));
    }),

    ("a new physical console session starts a fresh timeout", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);

        tracker.ObserveNoUser(start, Minutes(1), 1, Utc(-10));

        Expect(
            NoUserObservation.ConsoleSessionChanged,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 2, Utc(50)));

        Expect(
            NoUserObservation.Waiting,
            tracker.ObserveNoUser(start.AddSeconds(61), Minutes(1), 2, Utc(50)));
    }),

    ("logging on clears the previous no-user timeout", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);

        tracker.ObserveNoUser(start, Minutes(1), 1, Utc(-10));
        tracker.ObserveLoggedOnUser();

        Expect(false, tracker.IsMonitoring);
        Expect(
            NoUserObservation.Started,
            tracker.ObserveNoUser(start.AddMinutes(10), Minutes(1), 1, Utc(590)));
    }),

    ("input becoming available is treated as activity", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);

        tracker.ObserveNoUser(start, Minutes(1), 1, null);

        Expect(
            NoUserObservation.InputDetected,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, Utc(59)));
    }),

    ("console helper input resets no-user timeout", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);

        tracker.ObserveNoUser(start, Minutes(1), 1, null);

        Expect(true, tracker.ObserveExternalInput(start.AddSeconds(59), 1));
        Expect(
            NoUserObservation.Waiting,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, null));
        Expect(
            NoUserObservation.TimeoutReached,
            tracker.ObserveNoUser(start.AddSeconds(119), Minutes(1), 1, null));
    }),

    ("console helper ignores input from a different session", () =>
    {
        var tracker = new NoUserActivityTracker();
        var start = Utc(0);

        tracker.ObserveNoUser(start, Minutes(1), 1, null);

        Expect(false, tracker.ObserveExternalInput(start.AddSeconds(59), 2));
        Expect(
            NoUserObservation.TimeoutReached,
            tracker.ObserveNoUser(start.AddSeconds(60), Minutes(1), 1, null));
    }),

    ("unchanged and changed WTS timestamps are distinguished", () =>
    {
        var input = Utc(10);

        Expect(false, SessionInputActivity.HasChanged(input, input));
        Expect(false, SessionInputActivity.HasChanged(input, null));
        Expect(true, SessionInputActivity.HasChanged(null, input));
        Expect(true, SessionInputActivity.HasChanged(input, Utc(11)));
        Expect(true, SessionInputActivity.HasChanged(input, Utc(9)));
    }),

    ("locked timeout requires at least one logged-on session", () =>
    {
        Expect(
            false,
            MachineSessionPolicy.CanApplyLockedTimeout([]));
    }),

    ("locked timeout is allowed when every session is locked", () =>
    {
        Expect(
            true,
            MachineSessionPolicy.CanApplyLockedTimeout([true, true]));
    }),

    ("an unlocked session blocks every locked-session timeout", () =>
    {
        Expect(
            false,
            MachineSessionPolicy.CanApplyLockedTimeout([true, false, true]));
    }),

    ("an agent heartbeat cannot undo a WTS lock", () =>
    {
        Expect(
            true,
            MachineSessionPolicy.GetEffectiveLockedState(
                wtsIsLocked: true,
                serviceObservedLock: false,
                agentIsLocked: false));
        Expect(
            true,
            MachineSessionPolicy.GetEffectiveLockedState(
                wtsIsLocked: false,
                serviceObservedLock: false,
                agentIsLocked: true));
        Expect(
            true,
            MachineSessionPolicy.GetEffectiveLockedState(
                wtsIsLocked: false,
                serviceObservedLock: true,
                agentIsLocked: false));
        Expect(
            false,
            MachineSessionPolicy.GetEffectiveLockedState(
                wtsIsLocked: false,
                serviceObservedLock: false,
                agentIsLocked: false));
    }),

    ("presentation protection ignores SYSTEM-only execution requests", () =>
    {
        const uint esSystemRequired = 0x00000001;
        const uint esDisplayRequired = 0x00000002;

        Expect(
            false,
            PowerRequestPolicy.ShouldPauseForPresentation(
                esSystemRequired,
                presentationProtectionEnabled: true));
        Expect(
            true,
            PowerRequestPolicy.ShouldPauseForPresentation(
                esDisplayRequired,
                presentationProtectionEnabled: true));
        Expect(
            false,
            PowerRequestPolicy.ShouldPauseForPresentation(
                esDisplayRequired,
                presentationProtectionEnabled: false));
    }),

    ("a fresh active unlocked session blocks an idle shutdown", () =>
    {
        var now = Utc(100);

        Expect(
            true,
            InteractiveSessionPolicy.HasActiveSession(
                [new AgentSessionState(false, Utc(99), Utc(90))],
                now,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1)));
    }),

    ("locked idle and stale sessions do not block shutdown", () =>
    {
        var now = Utc(100);

        Expect(
            false,
            InteractiveSessionPolicy.HasActiveSession(
                [
                    new AgentSessionState(true, Utc(99), Utc(99)),
                    new AgentSessionState(false, Utc(99), Utc(0)),
                    new AgentSessionState(false, Utc(60), Utc(99))
                ],
                now,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1)));
    }),

    ("shutdown waits for the latest warning deadline", () =>
    {
        var now = Utc(100);

        Expect(
            Utc(110),
            WarningCoordinationPolicy.GetLatestActiveDeadline(
                [Utc(105), Utc(110), Utc(99)],
                now)!.Value);
    }),

    ("expired warning deadlines do not delay shutdown", () =>
    {
        var now = Utc(100);

        Expect<DateTime?>(
            null,
            WarningCoordinationPolicy.GetLatestActiveDeadline(
                [Utc(90), Utc(100)],
                now));
    }),

    ("a shared warning is shown before the local idle boundary", () =>
    {
        var now = Utc(100);

        Expect(
            true,
            WarningDisplayPolicy.ShouldShow(
                TimeSpan.FromSeconds(5),
                60,
                Utc(130),
                now));
    }),

    ("an expired shared warning cannot bypass the idle boundary", () =>
    {
        var now = Utc(100);

        Expect(
            false,
            WarningDisplayPolicy.ShouldShow(
                TimeSpan.FromSeconds(5),
                60,
                Utc(100),
                now));
    }),

    ("a shared warning uses its remaining countdown", () =>
    {
        Expect(
            6,
            WarningDisplayPolicy.GetVisibleSeconds(
                30,
                Utc(106),
                Utc(100)));
    }),

    ("warning cancellation requires a changed input tick", () =>
    {
        Expect(
            false,
            WarningDisplayPolicy.HasVerifiedLocalInput(null, 20));
        Expect(
            false,
            WarningDisplayPolicy.HasVerifiedLocalInput(20, null));
        Expect(
            false,
            WarningDisplayPolicy.HasVerifiedLocalInput(20, 20));
        Expect(
            true,
            WarningDisplayPolicy.HasVerifiedLocalInput(20, 21));
    })
};

var failures = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"[FAIL] {test.Name}: {ex.Message}");
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} logic test(s) failed.");
    return 1;
}

Console.WriteLine($"All {tests.Length} logic tests passed.");
return 0;

static DateTime Utc(int seconds) =>
    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        .AddSeconds(seconds);

static TimeSpan Minutes(int minutes) =>
    TimeSpan.FromMinutes(minutes);

static void Expect<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Expected {expected}, received {actual}.");
    }
}
