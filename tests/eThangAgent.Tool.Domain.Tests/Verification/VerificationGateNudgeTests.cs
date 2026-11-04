using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

/// <summary>Pins the turn gate's nudge contract at the gate level: advisory wording, one note
///     per dirty turn, and the fact that ANY command which exits 0 satisfies it - the harness
///     matches no command set, so a workspace of any shape is covered.</summary>
public class VerificationGateNudgeTests
{
  private static readonly DateTimeOffset Base = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeLedger : IVerificationLedger
  {
    public List<ShellExecutionRecord> Records { get; } = [];
    public void Append(ShellExecutionRecord record) => Records.Add(record);
    public IReadOnlyList<ShellExecutionRecord> Snapshot() => [.. Records];
  }

  private static (VerificationGate Gate, FakeLedger Ledger) MakeGate(bool enabled = true)
  {
    FakeLedger ledger = new();
    VerificationGate gate = new(ledger, enabled);
    return (gate, ledger);
  }

  private static readonly (string Path, DateTimeOffset ModifiedUtc)[] OneChange = [("a.cs", Base)];

  [Fact]
  public void DirtyTurn_NudgeIsAdvisory_NeverAnImperative()
  {
    (VerificationGate gate, _) = MakeGate();

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.StartsWith("[verification gate]", nudge, StringComparison.Ordinal);
    Assert.Contains("Note for your completion claim", nudge, StringComparison.Ordinal);
    Assert.Contains("not a stop instruction", nudge, StringComparison.Ordinal);
    // The old imperative read as a turn precondition and ended turns.
    Assert.DoesNotContain("Before claiming", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyLedger_NudgeDemandsNothingSpecific()
  {
    (VerificationGate gate, _) = MakeGate();

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.Contains("no command has exited 0 since the newest change", nudge, StringComparison.Ordinal);
    // No command set is named: the harness does not know this workspace's tooling.
    Assert.DoesNotContain("dotnet", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void FailedAttempt_NudgeStillAdvisory()
  {
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 1, Base.AddMinutes(-30), Base.AddMinutes(-29)));

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.Contains("Note for your completion claim", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void Verified_NudgeIsNull()
  {
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 0, Base.AddMinutes(5), Base.AddMinutes(6)));

    Assert.Null(gate.NudgeLine(OneChange));
  }

  [Fact]
  public void AnyCommand_ExitZero_Verifies()
  {
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["make", "check"], 0, Base.AddMinutes(5), Base.AddMinutes(6)));

    Assert.Null(gate.NudgeLine(OneChange));
  }

  [Fact]
  public void NothingChanged_NudgeIsNull()
  {
    (VerificationGate gate, _) = MakeGate();

    Assert.Null(gate.NudgeLine([]));
  }

  [Fact]
  public void DisabledGate_NudgeIsNull()
  {
    (VerificationGate gate, _) = MakeGate(enabled: false);

    Assert.Null(gate.NudgeLine(OneChange));
  }

  [Fact]
  public void WarningLine_NamesTheLastSuccessfulCommand_OrNone()
  {
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["dotnet", "build"], 0, Base.AddMinutes(-30), Base.AddMinutes(-29)));

    string line = VerificationGate.WarningLine(gate.Evaluate(OneChange));

    Assert.Contains("last successful command: dotnet build", line, StringComparison.Ordinal);
    Assert.Contains("1 file(s) changed", line, StringComparison.Ordinal);
    Assert.DoesNotContain("default set", line, StringComparison.Ordinal);
  }
}
