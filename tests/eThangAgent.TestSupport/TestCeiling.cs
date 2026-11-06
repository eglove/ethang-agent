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
///     <para>Override with <c>ETHANG_TEST_CEILING_SECONDS</c> when bisecting a genuine hang.</para></summary>
public static class TestCeiling
{
  /// <summary>The ceiling, resolved once per test process.</summary>
  public static TimeSpan Value { get; } = Resolve();

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
