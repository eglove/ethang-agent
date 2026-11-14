using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The app-level retention loop (issue #246): one sweep per interval, driven by a port
///     the storage side implements, with no model involvement and no tool surface.
///     <para>THE LOOP IS WHAT MAKES THE MECHANISM APP-LEVEL rather than a manual act, so what
///     these pins assert is the loop's own decisions: a disabled window reads as 'nothing to
///     do' rather than as a failure, a run that deleted nothing is not reported, and a reclaim
///     that failed IS reported even though the rows are gone.</para></summary>
public class RetentionLoopTests
{
  private sealed class StubRetention(Result<RetentionRun?> next) : IRetentionService
  {
    public int Runs { get; private set; }

    public Task<Result<RetentionRun?>> RunOnceAsync(CancellationToken ct = default)
    {
      Runs++;
      return Task.FromResult(next);
    }
  }

  private static RetentionRun Run(int rows, long before = 2000, long after = 1000, string? reclaim = null) =>
      new(TimeSpan.FromDays(30), new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
          new Dictionary<string, int> { ["agent_messages"] = rows },
          before, after, 0, reclaim, new DateTimeOffset(2026, 11, 14, 12, 1, 0, TimeSpan.Zero));

  /// <summary>A run that deleted rows is reported once, with its own numbers.</summary>
  [Fact]
  public async Task ARunThatDeletedRows_IsReported()
  {
    StubRetention retention = new(Result.Success<RetentionRun?>(Run(rows: 12)));
    List<RetentionRun> completed = [];
    RetentionLoop loop = new(retention, completed.Add, _ => Assert.Fail("no fault expected"));

    await loop.RunOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    RetentionRun report = Assert.Single(completed);
    Assert.Equal(12, report.RowsDeleted);
    Assert.Equal(1000, report.BytesReclaimed);
  }

  /// <summary>A disabled window is not an error and not a report: the loop asks the port, gets
  ///     null, and stops.</summary>
  [Fact]
  public async Task ADisabledWindow_ReportsNothing_AndFaultsNothing()
  {
    StubRetention retention = new(Result.Success<RetentionRun?>(null));
    RetentionLoop loop = new(retention, _ => Assert.Fail("nothing to report"),
        _ => Assert.Fail("not a fault"));

    await loop.RunOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(1, retention.Runs);
  }

  /// <summary>A run that found nothing to delete is silent: a daily 'nothing happened' line
  ///     would bury the runs that did something.</summary>
  [Fact]
  public async Task ARunThatDeletedNothing_IsSilent()
  {
    StubRetention retention = new(Result.Success<RetentionRun?>(Run(rows: 0)));
    RetentionLoop loop = new(retention, _ => Assert.Fail("nothing to report"),
        _ => Assert.Fail("not a fault"));

    await loop.RunOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>The reclaim failing is reported even though the rows are gone: a file that still
  ///     holds the pages has not done what the run was for, and the message must say so.
  ///     </summary>
  [Fact]
  public async Task AReclaimThatFailed_IsReported_EvenThoughTheRowsWent()
  {
    StubRetention retention = new(Result.Success<RetentionRun?>(
        Run(rows: 4, reclaim: "database is locked")));
    List<string> faults = [];
    RetentionLoop loop = new(retention, _ => Assert.Fail("a failed reclaim is not a completion"),
        faults.Add);

    await loop.RunOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    string fault = Assert.Single(faults);
    Assert.Contains("database is locked", fault, StringComparison.Ordinal);
    Assert.Contains("4 rows", fault, StringComparison.Ordinal);
  }

  /// <summary>A failed sweep is reported rather than thrown: the loop must survive for
  ///     tomorrow's attempt.</summary>
  [Fact]
  public async Task AFailedSweep_IsReported_AndTheLoopSurvives()
  {
    StubRetention retention = new(Result.Failure<RetentionRun?>(
        new DomainError("InvalidRetentionWindow", "bad window")));
    List<string> faults = [];
    RetentionLoop loop = new(retention, _ => Assert.Fail("no completion"), faults.Add);

    await loop.RunOnceAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("bad window", Assert.Single(faults));
  }
}
