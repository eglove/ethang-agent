using System.Diagnostics;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Output ceilings (spec D10): one run's captured output is bounded by two
///     caps. Past the truncate cap the HEAD is kept and an explicit marker names the
///     limit and the recovery — never a silent clip; past the kill cap the run is
///     stopped, because a program producing hundreds of megabytes of output is a
///     runaway, not a report. Both pipes feed ONE ceiling, so the bound is on the
///     run's TOTAL output rather than per stream. The unit tests pin the ceiling's own
///     arithmetic; the Shell tests pin the wiring end to end.</summary>
public sealed class OutputCeilingTests
{
  [Fact]
  public void Append_UnderTruncateCap_KeepsEverythingAndReportsNoTruncation()
  {
    OutputCeiling ceiling = new(truncateBytes: 100, killBytes: 1000);

    _ = ceiling.Append("hello");

    Assert.False(ceiling.Truncated);
    Assert.False(ceiling.Exceeded);
    Assert.Equal("hello", ceiling.Render());
  }

  [Fact]
  public void Append_PastTruncateCap_KeepsTheHeadAndMarksTruncation()
  {
    OutputCeiling ceiling = new(truncateBytes: 10, killBytes: 1000);

    _ = ceiling.Append(new string('a', 50));

    Assert.True(ceiling.Truncated);
    Assert.False(ceiling.Exceeded);
    Assert.Contains(OutputCeiling.TruncationMarker, ceiling.Render(), StringComparison.Ordinal);
    Assert.StartsWith("aaaaaaaaaa", ceiling.Render(), StringComparison.Ordinal);
  }

  [Fact]
  public void Append_PastKillCap_ReportsExceeded()
  {
    OutputCeiling ceiling = new(truncateBytes: 10, killBytes: 20);

    _ = ceiling.Append(new string('a', 50));

    Assert.True(ceiling.Exceeded);
  }

  [Fact]
  public void TruncationMarker_NamesTheLimitAndTheRecovery()
  {
    Assert.Contains("truncated", OutputCeiling.TruncationMarker, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("redirect", OutputCeiling.TruncationMarker, StringComparison.OrdinalIgnoreCase);
  }

  // The shared-budget rule at the unit level: the two streams draw from ONE room
  // budget, so the bound is on the run's total output, not per stream.
  [Fact]
  public void Append_BothStreams_DrawFromOneRoomBudget()
  {
    OutputCeiling ceiling = new(truncateBytes: 10, killBytes: 1000);

    _ = ceiling.Append("aaaaa");
    _ = ceiling.Append("bbbbb", isErrorStream: true);

    Assert.False(ceiling.Truncated);
    Assert.Equal("aaaaa", ceiling.Render());
    Assert.Equal("bbbbb", ceiling.RenderStderr());

    // One more character has no room left ANYWHERE: the budget is shared, so the
    // stdout chunk is dropped and stdout SAYS so — while stderr, untouched, stays
    // verbatim.
    _ = ceiling.Append("c");

    Assert.True(ceiling.Truncated);
    Assert.StartsWith("aaaaa", ceiling.Render(), StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, ceiling.Render(), StringComparison.Ordinal);
    Assert.Equal("bbbbb", ceiling.RenderStderr());
  }

  // The wiring, end to end: output under both caps is returned verbatim with no
  // marker, so a short result can never be mistaken for a truncated one.
  [Fact]
  public void Shell_OutputUnderTheCaps_IsReturnedVerbatimWithNoMarker()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 64 * 1024, killBytes: 1024 * 1024,
        TestContext.Current.CancellationToken);

    ShellResult result = globals.Shell("cmd", "/c", "echo hello");

    Assert.Equal(0, result.ExitCode);
    Assert.Equal("hello", result.Stdout.Trim());
    Assert.DoesNotContain(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
  }

  // Past the truncate cap the run still completes, but the model reads the head plus
  // the marker — truncation is never silent.
  [Fact]
  public void Shell_OutputPastTruncateCap_KeepsTheHeadAndMarksTheTruncation()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 1024 * 1024,
        TestContext.Current.CancellationToken);

    ShellResult result = globals.Shell("cmd", "/c", "for /L %i in (1,1,500) do @echo line-%i");

    Assert.Equal(0, result.ExitCode);
    Assert.StartsWith("line-1", result.Stdout, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
    Assert.True(result.Stdout.Length < 2048,
        $"the head must be bounded by the truncate cap; captured {result.Stdout.Length} chars");
  }

  // Past the kill cap the run is STOPPED, not merely truncated: a ceiling that only
  // clipped and let the 200,000-line loop run to completion would satisfy the exit code
  // and marker assertions too, so the elapsed bound is the assertion that the kill
  // actually happened. A real kill of this 200,000-line loop was MEASURED at ~149 ms;
  // 30 s is a generous bound that still catches a non-kill.
  [Fact]
  public void Shell_OutputPastKillCap_StopsTheRunAndReportsTheCeiling()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 64 * 1024,
        TestContext.Current.CancellationToken);
    Stopwatch elapsed = Stopwatch.StartNew();

    ShellResult result = globals.Shell("cmd", "/c", "for /L %i in (1,1,200000) do @echo line-%i");

    elapsed.Stop();
    Assert.Equal(-1, result.ExitCode);
    Assert.StartsWith("Error [OutputCeiling]", result.Stderr, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
        $"the run must be STOPPED, not allowed to finish; it took {elapsed.Elapsed}");
  }

  // stderr ALONE past the truncate cap: the clipped stream carries the marker and the
  // untouched (empty) stdout stays verbatim, so a quiet stdout never acquires a
  // truncation notice it did not earn.
  [Fact]
  public void Shell_StderrOnlyPastTruncateCap_MarksStderrAndLeavesStdoutVerbatim()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 1024 * 1024,
        TestContext.Current.CancellationToken);

    ShellResult result = globals.Shell("cmd", "/c", "for /L %j in (1,1,300) do @echo err-%j 1>&2");

    Assert.Equal(0, result.ExitCode);
    Assert.StartsWith("err-1", result.Stderr, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stderr, StringComparison.Ordinal);
    Assert.DoesNotContain(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
    Assert.True(result.Stderr.Length < 2048,
        $"the stderr head must be bounded by the truncate cap; captured {result.Stderr.Length} chars");
  }

  // Both pipes feed ONE ceiling, so the bound is on the run's TOTAL output: one short
  // stdout line plus a 300-line stderr under a 1 KB cap keep ~1 KB TOTAL for the pair,
  // not ~1 KB per stream. The one-second ping between the two writes makes the arrival
  // order deterministic, so the per-stream assertion is exact rather than a disjunction:
  // stdout's single short line FIT and carries NO marker, while stderr was clipped and
  // carries one.
  [Fact]
  public void Shell_BothPipesShareOneCeiling_SoTheBoundIsOnTheTotal()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 1024 * 1024,
        TestContext.Current.CancellationToken);

    ShellResult result = globals.Shell("cmd", "/c",
        "@echo out-1 & ping -n 2 127.0.0.1 > nul & for /L %j in (1,1,300) do @echo err-%j 1>&2");

    Assert.Equal(0, result.ExitCode);
    // Per stream, where the stream is known: stdout fit, stderr was clipped.
    Assert.Equal("out-1", result.Stdout.Trim());
    Assert.DoesNotContain(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stderr, StringComparison.Ordinal);
    // And the shared bound: the two heads together never exceed the one cap.
    int markerBudget = OutputCeiling.TruncationMarker.Length + 64;
    Assert.True(result.Stdout.Length + result.Stderr.Length <= 1024 + markerBudget,
        $"the bound is on the run's TOTAL output; captured {result.Stdout.Length} + {result.Stderr.Length} chars");
  }

  // The two caps are ORDERED, not independent: a kill cap at or below the truncate cap
  // stops the run at the very moment the head fills, so the head renders with no marker
  // at all and only the stderr error line keeps the result from being silent. The
  // invariant is validated at construction and names the offending field.
  [Theory]
  [InlineData(1024L, 1024L)]
  [InlineData(1024L, 512L)]
  public void ScriptCeilings_KillCapNotAboveTruncateCap_ThrowsNamingTheField(
      long truncateBytes, long killBytes)
  {
    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => new ScriptCeilings(1024L, 256, truncateBytes, killBytes));
    Assert.Equal("outputKillBytes", ex.ParamName);
  }

  // The rendered limit speaks the unit the cap actually counts. The caps are counted in
  // CHARACTERS of the decoded capture, so a limit rendered as a rounded "1 KB" would
  // tell the model the wrong unit to size its next run against.
  [Fact]
  public void Render_ClippedLimit_NamesTheCharacterUnit()
  {
    OutputCeiling ceiling = new(truncateBytes: 1024, killBytes: 4096);

    _ = ceiling.Append(new string('a', 2000));

    string rendered = ceiling.Render();
    Assert.Contains("characters", rendered, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("1024", rendered, StringComparison.Ordinal);
  }

  private static ScriptGlobals GlobalsWith(long truncateBytes, long killBytes, CancellationToken ct) =>
      new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
          ceilings: new ScriptCeilings(4L * 1024 * 1024 * 1024, 256, truncateBytes, killBytes),
          shellToken: ct);

  // Shell never resolves tools or invokes capabilities; an empty registry stub
  // satisfies ScriptTools' non-null contract.
  private static EmptyRegistry NoopRegistry() => new();

  private sealed class EmptyRegistry : ICapabilityRegistry
  {
    public Result<ResolvedCapability> Resolve(string nameOrRef) =>
        Result.Failure<ResolvedCapability>(new DomainError("NotFound", "unused"));

    public IReadOnlyList<ProviderCapabilities> Providers => [];

    public Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");
  }
}
