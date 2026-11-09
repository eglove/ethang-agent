namespace eThangAgent.TestSupport;

/// <summary>The one wall-clock bound a test may wait on. A test asserts that work SETTLES,
///     never that it settles within a duration tuned to an idle machine: under full-suite
///     parallelism the machine is saturated by the suite's own child-host processes, so a
///     per-test bound expires before the work does and the assertion proves nothing about the
///     code under test.
///     <para>A ceiling is a failure DETECTOR, not a latency contract. It exists so a broken
///     test fails instead of hanging the run, and it is set high enough that a loaded machine
///     cannot reach it. A test whose subject really is latency says so with
///     <c>[Trait("Category", "LatencyContract")]</c> and is exempt from this rule.</para>
///     <para>Override with <c>ETHANG_TEST_CEILING_SECONDS</c> when bisecting a genuine hang.</para>
///     <para>THE SAME BOUND IS APPLIED TO EVERY TEST AUTOMATICALLY, by
///     <c>eThangAgent.TestSupport.CeilingTestFramework</c>: a test that never settles fails with
///     its own name and a stack trace instead of stalling the run. The two are one number on
///     purpose - a test that waits on <see cref="Value"/> and a test that is killed at it cannot
///     disagree about how long settling is allowed to take.</para></summary>
public static class TestCeiling
{
  /// <summary>The ceiling, resolved once per test process.</summary>
  public static TimeSpan Value { get; } = Resolve();

  /// <summary>How long a test may RUN before it is failed, as the whole milliseconds xUnit's
  ///     per-test timeout takes. Zero means 'no automatic bound', which is the only honest
  ///     answer when the configured ceiling cannot be represented.
  ///     <para>STRICTLY ABOVE <see cref="Value"/>, NEVER EQUAL TO IT. A test is allowed to wait
  ///     the whole ceiling for something to settle - several do, by waiting on a token built
  ///     from <see cref="Value"/> - and it then needs time to observe the result and tear down.
  ///     A bound equal to the ceiling would kill those tests at the exact instant their own wait
  ///     expires, turning a passing test into a timeout and reporting a hang that is not there.
  ///     The margin is what makes the bound a detector for tests that never finish rather than a
  ///     deadline for tests that finish slowly.</para></summary>
  public static int TestBudgetMilliseconds => Value <= TimeSpan.Zero
      ? 0
      : (int)Math.Min(int.MaxValue, (Value + TeardownMargin).TotalMilliseconds);

  /// <summary>The room a test gets after its own ceiling expires: enough to observe the
  ///     cancellation, assert, and dispose. It is a fixed allowance rather than a fraction of
  ///     the ceiling so a large configured ceiling does not inflate the wait for a real hang.
  ///     </summary>
  private static readonly TimeSpan TeardownMargin = TimeSpan.FromSeconds(30);

  private static TimeSpan Resolve()
  {
    string? configured = Environment.GetEnvironmentVariable("ETHANG_TEST_CEILING_SECONDS");
    return string.IsNullOrWhiteSpace(configured) ? TimeSpan.FromSeconds(120) : Parse(configured);
  }

  private static TimeSpan Parse(string configured)
  {
    return double.TryParse(configured, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds)
        && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : throw new InvalidOperationException(
            "ETHANG_TEST_CEILING_SECONDS must be a positive number of seconds, not '" + configured + "'.");
  }
}
