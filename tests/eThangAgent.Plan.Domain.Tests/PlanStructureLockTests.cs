using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>The plan a chain executes must not be rewritten underneath it. A structure change -
///     inserting, moving, or removing a step - renumbers the positions the chain's claim binds to,
///     so the session holding step N silently starts completing somebody else's work.
///     <para>2026-11-16: a planner inserted a review step at position 1 AFTER starting its chain,
///     every implementation step shifted by one, and the review session had been seeded with a
///     brief naming the OLD numbers. The work was abandoned and the chain restarted from the
///     corrected plan. Refusing the edit is the cheaper exit.</para>
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

  private sealed class FakeLock(int? executingChain) : IPlanExecutionLock
  {
    public int Calls { get; private set; }

    public Task<int?> ExecutingChainAsync(int planId, CancellationToken ct = default)
    {
      ct.ThrowIfCancellationRequested();
      Calls++;
      Assert.Equal(1, planId); // the lock is asked about the plan being edited, never another
      return Task.FromResult(executingChain);
    }
  }

  private static async Task<(PlanCapabilityProvider Provider, FakeLock Lock)> SeededAsync(
      int? executingChain)
  {
    FakeLock executionLock = new(executingChain);
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()),
        () => "root-session-1", executionLock: executionLock);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" }, { \"title\": \"two\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return (provider, executionLock);
  }

  [Fact]
  public async Task AddStep_WhileAChainExecutesThePlan_IsRefused_NamingTheChain()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(executingChain: 9);

    CapabilityInvocationResult result = await provider.InvokeAsync("add-step",
        /*lang=json,strict*/"{ \"id\": 1, \"title\": \"three\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("PlanStructureLocked", result.Content, StringComparison.Ordinal);
    Assert.Contains("chain #9", result.Content, StringComparison.Ordinal);
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
