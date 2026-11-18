using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in finally blocks is deliberate (CA1031).
#pragma warning disable CA1031, S108 // Do not catch general exception types

namespace eThangAgent.Desktop.Tests;

/// <summary>REGRESSION (2026-11-18 job batch): the advance the shell builds carried no review
///     service.
///     <para><see cref="ChainAdvanceFactory.For"/> constructed the service with two arguments,
///     so the review parameter took its default of null. Every review step's completion was then
///     refused ChainReviewMissing - the verdict was on the record and the cursor could not see
///     it - so a review step could never settle and the chain stopped there permanently. The
///     batch abandoned three chains over it, one of them with its work complete and verified.
///     </para>
///     <para>THE UNIT TESTS PASSED THROUGHOUT because they construct the service with a review
///     store by hand. What was never tested is the WIRING - the one place that decides whether a
///     real session's advance can read a verdict at all - so that is what this pins.</para>
///     </summary>
// JOINS THE DESKTOP E2E COLLECTION: ETHANG_AGENT_DB is process-wide, so a class that sets it
// must never race the other env-var classes. Outside this collection it rewrites the database
// path out from under a concurrently running E2E and fails THAT test with a store it never asked
// for - which is what a sibling class did before it was moved here.
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class ChainAdvanceFactoryReviewWiringTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", BaseUrl),
      new AgentRunOptions(null, 2));

  [Fact]
  public async Task TheAdvanceTheShellBuilds_ActsOnARecordedVerdict()
  {
    string dbPath = Path.Combine(TestTempRoot.Root, $"ethang-advance-{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", dbPath);
    AgentSessionFactory factory = new(Settings());
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-advance-ws");
    Result<AgentSession> opened = default!;
    try
    {
      opened = await factory.CreateAsync(workspace.FullName, Providers.OpenRouter,
          ct: TestContext.Current.CancellationToken);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      AgentSession session = opened.Value;

      // THE PRODUCTION WIRING, exactly as DesktopHost builds it: no test-only seam.
      ChainAdvanceService? advance = ChainAdvanceFactory.For(session, () => null);
      Assert.NotNull(advance);

      ChainService chains = session.Services.GetRequiredService<ChainService>();
      DateTimeOffset now = DateTimeOffset.UtcNow;
      Chain chain = Must(await chains.CreateAsync(new ChainCreation(workspace.FullName, 3, 1, "t",
          session.RootId.ToString(),
          [new ChainStepSeed(1, ChainStepRole.Implement), new ChainStepSeed(2, ChainStepRole.Review)]),
          now, TestContext.Current.CancellationToken).ConfigureAwait(true));
      _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-impl", now,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
      _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Done,
          ChainActor.Session("session-impl"), null, now,
          TestContext.Current.CancellationToken).ConfigureAwait(true));
      _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-review", now,
          TestContext.Current.CancellationToken).ConfigureAwait(true));

      // THE REVIEWER RECORDS A PASS through the container's own service - the same write the
      // chain.judge action performs.
      ChainReviewService reviews = session.Services.GetRequiredService<ChainReviewService>();
      _ = Must(await reviews.RecordAsync("session-review",
          new ChainReviewRecord(ChainReviewVerdict.Pass, []), now,
          TestContext.Current.CancellationToken).ConfigureAwait(true));

      Result<ChainAdvance> advanced = await advance.OnCompletionAsync("session-review", planId: 3,
          now, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(advanced.IsSuccess, advanced.Error?.Message);

      // THE RECORDED PASS IS ACTED ON. With no review reader the cursor answers
      // ChainReviewMissing here and the review step can never settle; with one it advances.
      Assert.NotEqual(ChainReviewCode.NoVerdict, advanced.Value.Code);
    }
    finally
    {
      if (opened.IsSuccess)
      {
        _ = await opened.Value.TryDisposeContainerAsync();
      }

      // CLEARED BEFORE THE FILE IS TOUCHED, the same order the sibling env-var classes use: a
      // pooled connection keeps the file open, and a variable left set would point the next
      // class at a database this one just deleted.
      Environment.SetEnvironmentVariable("ETHANG_AGENT_DB", null);
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      TryDelete(dbPath);
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
      // best effort: a connection may still be draining.
    }
  }
}
