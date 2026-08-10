using IdleShutdown.ServiceApp;
using IdleShutdown.AgentApp;

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
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Expected {expected}, received {actual}.");
    }
}
