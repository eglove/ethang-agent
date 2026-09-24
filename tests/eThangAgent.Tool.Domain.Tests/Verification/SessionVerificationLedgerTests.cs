using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

public class SessionVerificationLedgerTests
{
  private static ShellExecutionRecord Record(int exitCode = 0, string exe = "dotnet") =>
      new([exe, "test"], exitCode,
          DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

  [Fact]
  public void Append_Then_Snapshot_ReturnsRecordIntact()
  {
    SessionVerificationLedger ledger = new();
    ShellExecutionRecord r = Record();

    ledger.Append(r);
    IReadOnlyList<ShellExecutionRecord> snap = ledger.Snapshot();

    ShellExecutionRecord only = Assert.Single(snap);
    Assert.Equal(r.Tokens, only.Tokens);
    Assert.Equal(r.ExitCode, only.ExitCode);
    Assert.Equal(r.StartedUtc, only.StartedUtc);
    Assert.Equal(r.FinishedUtc, only.FinishedUtc);
  }

  [Fact]
  public void Snapshot_IsDefensiveCopy_MutationsDoNotLeak()
  {
    SessionVerificationLedger ledger = new();
    ledger.Append(Record());

    List<ShellExecutionRecord> first = [.. ledger.Snapshot()];
    first.Clear();
    IReadOnlyList<ShellExecutionRecord> second = ledger.Snapshot();

    Assert.NotEmpty(second);
  }

  [Fact]
  public void Repeated_Appends_AllLand()
  {
    SessionVerificationLedger ledger = new();
    for (int i = 0; 8 > i; i++)
    {
      ledger.Append(Record());
    }

    Assert.Equal(8, ledger.Snapshot().Count);
  }
}
