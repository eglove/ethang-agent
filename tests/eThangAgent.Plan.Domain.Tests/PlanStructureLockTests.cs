using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>A plan a chain executes may be APPENDED to but not renumbered underneath it. A chain
///     binds its claims to plan step POSITIONS, so inserting, moving, or removing a step renumbers
///     the positions the running sessions already hold. A step appended past every position the
///     chain holds shifts nothing and is permitted; the lock is bounded by the chain's reach.
///     <para>A session outside any chain is unaffected, and so is a chain that has already
///     finished: its positions can no longer be re-read by anybody.</para></summary>
public class PlanStructureLockTests
{
  private sealed class FakeStore : IPlanStore
  {
    private readonly Dictionary<int, Plan> _plans = [];
    private int _next = 1;

    public Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default)
    {
      Plan saved = draft with { Id = _next++, Version = 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_plans.TryGetValue(id, out Plan? p)
            ? Result.Success(p)
            : Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{id}")));

    public Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new PlanCatalog([.. _plans.Values], [])));

    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
    {
      if (!_plans.TryGetValue(plan.Id, out Plan? current))
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("PlanNotFound", "gone")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("VersionConflict", "stale")));
      }

      Plan saved = plan with { Version = current.Version + 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
  }

  /// <summary>A chain executing the plan and holding positions up to <c>lastPosition</c>, or no
  ///     chain at all when <paramref name="executingChain"/> is null.</summary>
  private sealed class FakeLock(int? executingChain, int lastPosition = 2) : IPlanExecutionLock
  {
    public int Calls { get; private set; }

    public Task<PlanExecutionLock?> ExecutionAsync(int planId, CancellationToken ct = default)
    {
      ct.ThrowIfCancellationRequested();
      Calls++;
      Assert.Equal(1, planId); // the lock is asked about the plan being edited, never another
      return Task.FromResult<PlanExecutionLock?>(executingChain is { } chainId
          ? new PlanExecutionLock(chainId, lastPosition)
          : null);
    }
  }

  private static async Task<(PlanCapabilityProvider Provider, FakeLock Lock)> SeededAsync(
      int? executingChain, int lastPosition = 2)
  {
    FakeLock executionLock = new(executingChain, lastPosition);
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()),
        () => "root-session-1", executionLock: executionLock);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" }, { \"title\": \"two\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return (provider, executionLock);
  }

  [Fact]
  public async Task SetStatus_Completed_WhileAChainIsStillRunning_IsRefusedNamingTheChain()
  {
    // A PLAN MARKED COMPLETE WHILE ITS CHAIN RUNS CLAIMS WORK IS DONE THAT NOTHING HAS FINISHED,
    // and the chain then advances under a plan nobody is watching for. Batch #222 did exactly
    // that: the plan read Completed while both its chains were still Running.
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains(PlanCapabilityProvider.LiveChainCode, result.Content, StringComparison.Ordinal);
    Assert.Contains("chain #9", result.Content, StringComparison.Ordinal);
    // THE REFUSAL NAMES THE EXIT, and leaves the stored status untouched.
    Assert.Contains("chain.abandon", result.Content, StringComparison.Ordinal);
    CapabilityInvocationResult shown = await provider.InvokeAsync("show",
        /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Contains("| Active |", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SetStatus_Completed_WithNoChainRunning_Succeeds()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: null);
    // THE PLAN'S OWN INVARIANT STILL HOLDS: every step settled. This test is about the CHAIN check
    // not firing when no chain is running, so the steps are settled to leave only that question.
    foreach (int position in new[] { 1, 2 })
    {
      _ = await provider.InvokeAsync("update-step",
          $"{{ \"id\": 1, \"position\": {position}, \"status\": \"Skipped\" }}",
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[plan] #1 is now Completed", result.Content);
  }

  [Fact]
  public async Task AddStep_AppendingPastTheChainsLastPosition_IsAllowed()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Equal("[plan] added step 3 to #1", result.Content);
  }

  [Fact]
  public async Task AddStep_AppendingPastTheChainsLastPosition_IsAllowed_WithExplicitPosition()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\", \"position\": 3 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }

  [Fact]
  public async Task AddStep_AppendingPastTheChainsLastPosition_IsAllowed_WhenTheChainTrailsThePlan()
  {
    // A chain holds the positions it names, which need not reach the plan's end.
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9, lastPosition: 1);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\", \"position\": 2 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }

  [Fact]
  public async Task AddStep_InsertingAtOrBeforeTheChainsLastPosition_IsRefused_NamingTheChainAndTheFirstSafePosition()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\", \"position\": 1 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("PlanStructureLocked", result.Content, StringComparison.Ordinal);
    Assert.Contains("chain #9", result.Content, StringComparison.Ordinal);
    Assert.Contains("position 3", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AddStep_InsertingInsideTheChainsRange_IsRefused()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\", \"position\": 2 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("PlanStructureLocked", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MoveStep_WhileAChainExecutesThePlan_IsRefused()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("move-step",
        /*lang=json,strict*/"{ \"id\": 1, \"from\": 1, \"to\": 2 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("PlanStructureLocked", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemoveStep_WhileAChainExecutesThePlan_IsRefused()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("remove-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 2 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("PlanStructureLocked", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStepDetail_WhileAChainExecutesThePlan_IsAllowed()
  {
    // The lock is about POSITIONS. Rewriting a step's text in place leaves every claim where it
    // was, so a step's session may still sharpen its own brief.
    (PlanCapabilityProvider provider, FakeLock executionLock) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"detail\": \"sharper words\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Equal(0, executionLock.Calls);
  }

  [Fact]
  public async Task AddStep_WithNoChainOnThePlan_BehavesExactlyAsBefore()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: null);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }

  [Fact]
  public async Task AddStep_WithNoLockWired_BehavesExactlyAsBefore()
  {
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()), () => "root-session-1");
    _ = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"two\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }
}
