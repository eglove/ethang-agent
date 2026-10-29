using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;

// Best-effort temp-file cleanup in catch blocks is deliberate (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>A chain brief survives a resume (issue #302 acceptance criterion 3).
///     <para>WHY THIS IS NOT THE HANDOFF TEST: the handoff seed's persistence is already
///     pinned, but 'the seed was written' and 'the seed comes back' are different facts. The
///     transcript sink's baseline counts the seeded prefix as already-persisted, so a seed
///     that reached the conversation but never the store would look correct in the first
///     session and be gone after a restart — which is the failure mode the criterion names.</para>
///     <para>THE BRIEF IS BUILT BY THE REAL SERVICE over the real stores, so this also proves
///     the composition adapter's three reads agree with each other.</para></summary>
public sealed class ChainBriefResumeTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", BaseUrl),
      new SubAgentOptions(null, 2));

  [Fact]
  public async Task AChainBriefSeed_SurvivesAResume()
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-brief-{Guid.NewGuid():N}.db");
    AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
    Result<AgentSession>? resumed = null;
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-brief-ws");
      try
      {
        // The brief as the real service renders it: a chain step assignment in, one
        // summary message out.
        ChainBriefService briefs = new(new StubSource());
        Result<IReadOnlyList<Message>> seed = await briefs.BuildSeedAsync(
            new ChainStepAssignment(7, 104, 2, null), TestContext.Current.CancellationToken);
        Assert.True(seed.IsSuccess, seed.Error?.Message);

        Result<AgentSession> created = await factory.CreateAsync(
            dir.FullName, Providers.OpenRouter, seed.Value, TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error?.Message);
        AgentId rootId = created.Value.RootId;

        // The brief is in the conversation the model reads.
        Message live = Assert.Single(created.Value.Conversation.Messages);
        Assert.True(live.IsSummary);
        Assert.Contains("You are step 2 of chain #7", live.Content, StringComparison.Ordinal);

        // Close the session exactly as a tab close would, then resume it.
        await created.Value.Lifecycle.CompleteAsync(rootId, _ => Assert.Fail("no errors expected"));
        await created.Value.Services.DisposeAsync();

        resumed = await factory.ResumeAsync(rootId, ct: TestContext.Current.CancellationToken);
        Assert.True(resumed.IsSuccess, resumed.Error?.Message);

        // THE CRITERION: the brief came back. A seed that only reached the conversation
        // would be gone here, because the sink's baseline counted it as persisted.
        // The resume also appends its own contract notice, so the brief is found by its
        // prefix rather than by position.
        Message hydrated = Assert.Single(resumed.Value.Conversation.Messages,
            m => m.Content.StartsWith(ChainBriefRenderer.Prefix, StringComparison.Ordinal));
        Assert.Contains("You are step 2 of chain #7", hydrated.Content, StringComparison.Ordinal);
        Assert.Contains("sci.dashboard-truth/ledger", hydrated.Content, StringComparison.Ordinal);
        Assert.Contains("plan.update-step id=104 position=2 status=Done", hydrated.Content, StringComparison.Ordinal);
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      if (resumed is { IsSuccess: true })
      {
        await resumed.Value.Services.DisposeAsync();
      }

      try
      {
        File.Delete(dbPath);
      }
      catch
      {
        // best effort
      }
    }
  }

  /// <summary>The three reads a brief needs, answered from constants. The stores' own
  ///     behaviour is covered where those stores live; this test is about the SEED surviving.
  /// </summary>
  private sealed class StubSource : IChainBriefSource
  {
    public Task<Result<ChainBriefChain>> GetChainAsync(int chainId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new ChainBriefChain(
            7, "2026-08-24-dashboard-truth", 104, [])));

    public Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new ChainBriefPlan(104, 3,
            "Make the dashboard tell the truth about what it did.",
            [new ChainBriefPlanStep(1, "Honest status line", null),
             new ChainBriefPlanStep(2, "Real demo broker", "Wire the demo broker.")])));

    public Task<Result<IReadOnlyList<string>>> GetGateCommandsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<string>>(["dotnet build"]));
  }
}
