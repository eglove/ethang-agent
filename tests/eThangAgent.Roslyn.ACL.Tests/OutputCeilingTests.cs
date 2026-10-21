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
    ScriptGlobals globals = GlobalsWith(truncateBytes: 64 * 1024, killBytes: 1024 * 1024);

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
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 1024 * 1024);

    ShellResult result = globals.Shell("cmd", "/c", "for /L %i in (1,1,500) do @echo line-%i");

    Assert.Equal(0, result.ExitCode);
    Assert.StartsWith("line-1", result.Stdout, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
    Assert.True(result.Stdout.Length < 2048,
        $"the head must be bounded by the truncate cap; captured {result.Stdout.Length} chars");
  }

  // Past the kill cap the run is STOPPED: exit -1 and the ceiling error contract, never
  // a bare non-zero exit the model would read as the program's own failure.
  [Fact]
  public void Shell_OutputPastKillCap_StopsTheRunAndReportsTheCeiling()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 64 * 1024);

    ShellResult result = globals.Shell("cmd", "/c", "for /L %i in (1,1,200000) do @echo line-%i");

    Assert.Equal(-1, result.ExitCode);
    Assert.StartsWith("Error [OutputCeiling]", result.Stderr, StringComparison.Ordinal);
    Assert.Contains(OutputCeiling.TruncationMarker, result.Stdout, StringComparison.Ordinal);
  }

  // Both pipes feed ONE ceiling: 300 stdout lines plus 300 stderr lines under a 1 KB
  // cap keep ~1 KB TOTAL, not ~1 KB per stream, and the clipped stream says so.
  [Fact]
  public void Shell_BothPipesShareOneCeiling_SoTheBoundIsOnTheTotal()
  {
    ScriptGlobals globals = GlobalsWith(truncateBytes: 1024, killBytes: 1024 * 1024);

    ShellResult result = globals.Shell("cmd", "/c",
        "for /L %i in (1,1,300) do @echo out-%i & for /L %j in (1,1,300) do @echo err-%j 1>&2");

    Assert.Equal(0, result.ExitCode);
    int markerBudget = 2 * (OutputCeiling.TruncationMarker.Length + 64);
    Assert.True(result.Stdout.Length + result.Stderr.Length <= 1024 + markerBudget,
        $"the bound is on the run's TOTAL output; captured {result.Stdout.Length} + {result.Stderr.Length} chars");
    Assert.True(result.Stdout.Contains(OutputCeiling.TruncationMarker, StringComparison.Ordinal)
        || result.Stderr.Contains(OutputCeiling.TruncationMarker, StringComparison.Ordinal),
        "the clipped stream must carry the truncation marker");
  }

  private static ScriptGlobals GlobalsWith(long truncateBytes, long killBytes) =>
      new(NoopRegistry(), Path.GetTempPath(), Path.GetTempPath(),
          ceilings: new ScriptCeilings(4L * 1024 * 1024 * 1024, 256, truncateBytes, killBytes));

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
