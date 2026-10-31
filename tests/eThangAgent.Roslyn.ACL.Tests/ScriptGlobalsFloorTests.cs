using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Issue #304: the universal floor is enforced at the ONE place an external command is
///     spawned from a script, and the refusal is a readable ShellResult rather than a throw.
///     <para>WHY NOT A THROW: the act is a line inside a program. Throwing there would kill the
///     program mid-way and lose whatever it had already done, which is neither the refusal nor
///     the park the issue asks for. The program keeps running and reads why; the trip is
///     recorded so the turn can park the chain once.</para>
///     <para>THE SPAWN IS NEVER ATTEMPTED, which is what these assert through a command that
///     WOULD have produced output if it had run.</para></summary>
public class ScriptGlobalsFloorTests
{
  private static ScriptGlobals Globals(ChainFloorLedger ledger) =>
      new(new EmptyRegistry(), Path.GetTempPath(), Path.GetTempPath(), floor: ledger);

  // THE ACCEPTANCE CRITERION: an act on the floor is refused before anything spawns.
  [Fact]
  public void Shell_WithAForcePush_IsRefusedAndNothingRuns()
  {
    ChainFloorLedger ledger = new();
    ScriptGlobals globals = Globals(ledger);

    ShellResult result = globals.Shell("git", "push", "--force", "origin", "main");

    Assert.NotEqual(0, result.ExitCode);
    Assert.Contains("chain floor", result.Stderr, StringComparison.Ordinal);
    Assert.Contains("force-push", result.Stderr, StringComparison.Ordinal);
    Assert.Equal(string.Empty, result.Stdout);
  }

  // The trip is RECORDED, which is what the park reads: a refusal that left no trace would
  // leave the chain running on an act nobody could see.
  [Fact]
  public void Shell_WithAForcePush_RecordsTheTrip()
  {
    ChainFloorLedger ledger = new();
    ScriptGlobals globals = Globals(ledger);

    ShellResult result = globals.Shell("git", "push", "--force");

    ChainFloorTrip trip = Assert.Single(ledger.Trips);
    Assert.Equal("force-push", trip.RuleId);
    Assert.Equal("git push --force", trip.Command);
    Assert.Equal(result.Stderr, trip.Refusal);
  }

  // EVERY floor rule is enforced at this one site, not just the first: a release publication
  // is exactly as unlicensed as a force-push.
  [Theory]
  [InlineData("gh", "publish", "release", "create", "v1.0")]
  [InlineData("git", "history-rewrite", "commit", "--amend", "-m")]
  [InlineData("npm", "publish", "publish", "--access", "public")]
  public void Shell_RefusesEveryFloorRule(string exe, string expectedRuleId, string arg1,
      string arg2, string arg3)
  {
    ChainFloorLedger ledger = new();
    ScriptGlobals globals = Globals(ledger);

    _ = globals.Shell(exe, arg1, arg2, arg3);

    Assert.Equal(expectedRuleId, Assert.Single(ledger.Trips).RuleId);
  }

  // AN ORDINARY COMMAND IS UNTOUCHED: the floor must not become a gate on all work.
  [Fact]
  public void Shell_WithAnOrdinaryCommand_RunsAndRecordsNothing()
  {
    ChainFloorLedger ledger = new();
    ScriptGlobals globals = Globals(ledger);

    ShellResult result = globals.Shell("cmd", "/c", "exit 0");

    Assert.Equal(0, result.ExitCode);
    Assert.False(ledger.Tripped);
  }

  // A HOST THAT WIRES NO FLOOR refuses nothing - the pre-#304 behaviour, and the honest one
  // for a session outside any chain.
  [Fact]
  public void Shell_WithNoFloorWired_RefusesNothing()
  {
    ScriptGlobals globals = new(new EmptyRegistry(), Path.GetTempPath(), Path.GetTempPath());

    ShellResult result = globals.Shell("cmd", "/c", "exit 0");

    Assert.Equal(0, result.ExitCode);
    Assert.Null(globals.Floor);
  }

  // Shell never resolves tools or invokes capabilities; an empty registry stub satisfies
  // ScriptTools' non-null contract.
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
