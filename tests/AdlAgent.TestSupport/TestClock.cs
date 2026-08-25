namespace AdlAgent.TestSupport;

/// <summary>The instant every harness starts at.</summary>
/// <remarks>
/// Named rather than parsed in two places because the fixtures now depend on
/// it as well as the clock does. A station link is built saying when ADL last
/// received something for it, and "recently" only means anything against the
/// clock the machine judging it is running on -- the two drifting apart would
/// make every fixture station quiet, everywhere, for no reason a test could
/// see.
/// </remarks>
public static class TestClock
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-21T09:00:00Z");
}
