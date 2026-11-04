using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

// The harness ships no command set: ANY command that exits 0 is verification evidence, so a
// workspace of any shape is covered by whatever its session ran and watched succeed.
public class VerificationFreshnessAnyCommandTests
{
  private static readonly DateTimeOffset Base = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

  private static readonly (string Path, DateTimeOffset ModifiedUtc)[] OneChange = [("a.cs", Base)];

  private static ShellExecutionRecord Run(int exit, string[] tokens, DateTimeOffset start) =>
      new(tokens, exit, start, start.AddMinutes(1));

  private static bool Fresh(params ShellExecutionRecord[] ledger) =>
      VerificationFreshnessSpecification.IsSatisfiedBy(ledger, OneChange);

  [Fact]
  public void DotnetTest_ExitZero_Counts() =>
      Assert.True(Fresh(Run(0, ["dotnet", "test"], Base.AddMinutes(5))));

  [Fact]
  public void ArbitraryProjectTool_ExitZero_Counts() =>
      Assert.True(Fresh(Run(0, ["make", "check"], Base.AddMinutes(5))));

  [Fact]
  public void UnknownScript_ExitZero_Counts() =>
      Assert.True(Fresh(Run(0, ["C:\\tools\\verify.ps1"], Base.AddMinutes(5))));

  [Fact]
  public void AnyCommand_ExitNonzero_DoesNotCount() =>
      Assert.False(Fresh(Run(1, ["make", "check"], Base.AddMinutes(5))));

  [Fact]
  public void SuccessBeforeTheNewestChange_DoesNotCount() =>
      Assert.False(Fresh(Run(0, ["dotnet", "test"], Base.AddMinutes(-5))));

  [Fact]
  public void NothingChanged_Passes_EvenWithEmptyLedger() =>
      Assert.True(VerificationFreshnessSpecification.IsSatisfiedBy([], []));

  [Fact]
  public void NewestRun_IgnoresExitCode_AndPicksLatestStart()
  {
    ShellExecutionRecord older = Run(0, ["dotnet", "build"], Base);
    ShellExecutionRecord newer = Run(1, ["dotnet", "test"], Base.AddMinutes(5));

    Assert.Same(newer, VerificationFreshnessSpecification.NewestRun([older, newer]));
  }

  [Fact]
  public void NewestSuccessfulRun_SkipsFailures()
  {
    ShellExecutionRecord success = Run(0, ["dotnet", "build"], Base);
    ShellExecutionRecord failure = Run(1, ["dotnet", "test"], Base.AddMinutes(5));

    Assert.Same(success, VerificationFreshnessSpecification.NewestSuccessfulRun([success, failure]));
  }

  [Fact]
  public void NewestSuccessfulRun_NoneSucceeded_IsNull() =>
      Assert.Null(VerificationFreshnessSpecification.NewestSuccessfulRun(
          [Run(1, ["dotnet", "test"], Base)]));
}
