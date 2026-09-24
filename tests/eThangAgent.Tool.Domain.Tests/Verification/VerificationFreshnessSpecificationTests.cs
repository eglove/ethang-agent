using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

public class VerificationFreshnessSpecificationTests
{
  private static readonly DateTimeOffset Base = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

  private static VerificationCommandSpecification Classifier() =>
      new(["dotnet test"]);

  private static ShellExecutionRecord Run(DateTimeOffset start, DateTimeOffset end, int exit = 0) =>
      new(["dotnet", "test"], exit, start, end);

  private static VerificationFreshnessSpecification Fresh() => new(Classifier());

  [Fact]
  public void Run_After_LastEdit_Passes()
  {
    // edit 10:00, run 10:05-10:10 -> fresh
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base)];
    ShellExecutionRecord[] ledger = [Run(Base.AddMinutes(5), Base.AddMinutes(10))];
    Assert.True(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void Run_Before_LastEdit_Fails()
  {
    // run 10:00-10:05, edit 10:30 -> stale
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base.AddMinutes(30))];
    ShellExecutionRecord[] ledger = [Run(Base, Base.AddMinutes(5))];
    Assert.False(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void Edit_During_Run_Fails_StartComparison()
  {
    // started 10:00, edit landed 10:00:30 mid-run, finished 10:01 -> stale
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base.AddSeconds(30))];
    ShellExecutionRecord[] ledger = [Run(Base, Base.AddMinutes(1))];
    Assert.False(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void NothingChanged_Passes_EvenWithEmptyLedger() =>
      Assert.True(Fresh().IsSatisfiedBy([], []));

  [Fact]
  public void FailedRun_NeverCounts_EvenWhenFresh()
  {
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base)];
    ShellExecutionRecord[] ledger = [Run(Base.AddMinutes(5), Base.AddMinutes(10), exit: 1)];
    Assert.False(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void NonVerificationRun_NeverCounts()
  {
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base)];
    ShellExecutionRecord[] ledger = [new ShellExecutionRecord(["git", "status"], 0, Base.AddMinutes(5), Base.AddMinutes(6))];
    Assert.False(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void NewestChange_DrivesThePredicate()
  {
    // edit1 10:00, run 10:05, edit2 10:30 -> newest edit is AFTER the run: stale
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "a.cs", ModifiedUtc: Base), (Path: "b.cs", ModifiedUtc: Base.AddMinutes(30))];
    ShellExecutionRecord[] ledger = [Run(Base.AddMinutes(5), Base.AddMinutes(10))];
    Assert.False(Fresh().IsSatisfiedBy(ledger, changed));
  }

  [Fact]
  public void UntrackedFiles_CountLikeAnyChange()
  {
    (string Path, DateTimeOffset ModifiedUtc)[] changed = [(Path: "notes.txt", ModifiedUtc: Base)];
    ShellExecutionRecord[] ledger = [Run(Base.AddMinutes(5), Base.AddMinutes(6))];
    Assert.True(Fresh().IsSatisfiedBy(ledger, changed));
  }
}
