using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

/// <summary>Pins the turn gate's nudge contract at the gate level (issue #256):
///     advisory wording, the no-known-command escape, and the branch decision -
///     ANY verification-class attempt in the ledger (success or failure) means a
///     command is known, because a model that watched 'dotnet test' fail cannot
///     honestly be told no verification command is known.</summary>
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
    VerificationGate gate = new(
        ledger,
        new VerificationFreshnessSpecification(new VerificationCommandSpecification(["dotnet test"])),
        enabled);
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
  public void EmptyLedger_NudgeNamesNoKnownCommand()
  {
    (VerificationGate gate, _) = MakeGate();

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.Contains("no verification command is known for this workspace", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void StaleSuccessfulRun_NudgeTakesKnownCommandBranch()
  {
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 0, Base.AddMinutes(-30), Base.AddMinutes(-29)));

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.DoesNotContain("no verification command is known", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void FailedVerificationAttempt_StillKnownCommand_NeverTheEscape()
  {
    // The model ran the verification command and watched it FAIL: the command
    // exists. Telling it "no verification command is known" would contradict its
    // own transcript; the known-command branch (make the run succeed) is honest.
    (VerificationGate gate, FakeLedger ledger) = MakeGate();
    ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 1, Base.AddMinutes(-30), Base.AddMinutes(-29)));

    string? nudge = gate.NudgeLine(OneChange);

    Assert.NotNull(nudge);
    Assert.DoesNotContain("no verification command is known", nudge, StringComparison.Ordinal);
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
  public void HasKnownCommand_EmptyLedger_False()
  {
    VerificationCommandSpecification spec = new(["dotnet test"]);

    Assert.False(spec.HasKnownCommand([]));
  }

  [Fact]
  public void HasKnownCommand_FailedVerificationRun_True()
  {
    VerificationCommandSpecification spec = new(["dotnet test"]);
    ShellExecutionRecord[] ledger = [new ShellExecutionRecord(["dotnet", "test"], 1, Base, Base.AddMinutes(1))];

    Assert.True(spec.HasKnownCommand(ledger));
  }

  [Fact]
  public void HasKnownCommand_SuccessfulVerificationRun_True()
  {
    VerificationCommandSpecification spec = new(["dotnet test"]);
    ShellExecutionRecord[] ledger = [new ShellExecutionRecord(["dotnet", "test"], 0, Base, Base.AddMinutes(1))];

    Assert.True(spec.HasKnownCommand(ledger));
  }

  [Fact]
  public void HasKnownCommand_NonVerificationRun_False()
  {
    VerificationCommandSpecification spec = new(["dotnet test"]);
    ShellExecutionRecord[] ledger = [new ShellExecutionRecord(["git", "status"], 0, Base, Base.AddMinutes(1))];

    Assert.False(spec.HasKnownCommand(ledger));
  }
}
