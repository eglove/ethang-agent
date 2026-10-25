using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

// Issue #283 Part 2 at the capability boundary: the STRICT attach point. 'Completed' is
// a completion claim, so it needs a fresh passing gate; 'Abandoned' is the named exit
// and must always work.
public class PlanCompletionGateTests
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

    public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_plans.TryGetValue(id, out Plan? p)
            ? Result.Success(p)
            : Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{id}")));

    public Task<Result<PlanCatalog>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new PlanCatalog(
            [.. _plans.Values.Where(p => status is null || p.Status == status)], [])));

    public Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default)
    {
      if (!_plans.TryGetValue(plan.Id, out Plan? current))
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("PlanNotFound", $"no plan #{plan.Id}")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Plan>(new DomainError("VersionConflict",
            $"plan #{plan.Id} is at v{current.Version}")));
      }

      Plan saved = plan with { Version = current.Version + 1 };
      _plans[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }
  }

  private sealed class FixedGate(bool allowed, string message) : IPlanCompletionGate
  {
    public Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default) =>
        Task.FromResult((allowed, message));
  }

  private static async Task<PlanCapabilityProvider> SeededAsync(IPlanCompletionGate? gate)
  {
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()), () => "s1", gate);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return provider;
  }

  [Fact]
  public async Task Completed_WithStaleGate_FailsWithTypedCode()
  {
    PlanCapabilityProvider provider = await SeededAsync(
        new FixedGate(false, "[completion gate] the gate is stale; run state.checkgoal"));

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("CompletionGateNotFresh", result.Content, StringComparison.Ordinal);
    Assert.Contains("state.checkgoal", result.Content, StringComparison.Ordinal);
  }

  // The refusal must leave the plan untouched - a gate that half-applied would be worse
  // than no gate at all.
  [Fact]
  public async Task Completed_WithStaleGate_LeavesPlanActive()
  {
    PlanCapabilityProvider provider = await SeededAsync(new FixedGate(false, "stale"));
    _ = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    CapabilityInvocationResult shown = await provider.InvokeAsync("show",
        /*lang=json,strict*/"{ \"id\": 1 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Contains("| Active |", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Completed_WithFreshGate_Succeeds()
  {
    PlanCapabilityProvider provider = await SeededAsync(new FixedGate(true, "fresh"));

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("is now Completed", result.Content, StringComparison.Ordinal);
  }

  // Abandoned is NEVER gated: it is the exit every refusal points at.
  [Fact]
  public async Task Abandoned_WithStaleGate_StillSucceeds()
  {
    PlanCapabilityProvider provider = await SeededAsync(new FixedGate(false, "stale"));

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Abandoned\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("is now Abandoned", result.Content, StringComparison.Ordinal);
  }

  // A provider with no gate wired behaves exactly as before.
  [Fact]
  public async Task NoGateWired_CompletedSucceeds()
  {
    PlanCapabilityProvider provider = await SeededAsync(gate: null);

    CapabilityInvocationResult result = await provider.InvokeAsync("set-status",
        /*lang=json,strict*/"{ \"id\": 1, \"status\": \"Completed\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
  }
}
