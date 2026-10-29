using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>Issue #301 D3 at the MODEL-FACING surface: the plan provider refuses a Done on a
///     step the session does not hold, and passes a session outside any chain straight through.
///     The gate is faked here - the real rule is covered by the Composition's own tests.</summary>
public class PlanStepOwnershipGateTests
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

  private sealed class FakeGate(PlanStepOwnership answer) : IPlanStepOwnershipGate
  {
    public int Calls { get; private set; }

    public Task<PlanStepOwnership> EvaluateAsync(int planId, int position, string sessionId,
        CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(answer);
    }
  }

  private static async Task<(PlanCapabilityProvider Provider, FakeGate Gate)> SeededAsync(
      PlanStepOwnership answer)
  {
    FakeGate gate = new(answer);
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()),
        () => "root-session-1", completionGate: null, ownershipGate: gate);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return (provider, gate);
  }

  [Fact]
  public async Task UpdateStep_Done_WhenTheGateRefuses_RendersTheNamedCodeAndLeavesTheStepPending()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(PlanStepOwnership.Refuse(
        "ChainStepNotAssigned", "chain #4 assigned you step 2, not step 1."));

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ChainStepNotAssigned", result.Content, StringComparison.Ordinal);
    Assert.Contains("step 2", result.Content, StringComparison.Ordinal);

    // The refusal ran BEFORE the write, so the plan is untouched.
    CapabilityInvocationResult shown = await provider.InvokeAsync("show",
        /*lang=json,strict*/"{ \"id\": 1 }", TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Contains("Pending", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UpdateStep_Done_WhenTheGatePermits_WritesTheStep()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(PlanStepOwnership.Permit);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }

  [Fact]
  public async Task UpdateStep_ToSkipped_NeverConsultsTheGate()
  {
    // Skipped is the truthful exit the completion invariant depends on (#271); gating it
    // would make the honest answer unreachable.
    (PlanCapabilityProvider provider, FakeGate gate) = await SeededAsync(PlanStepOwnership.Refuse(
        "ChainStepNotAssigned", "never consulted"));

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Skipped\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
    Assert.Equal(0, gate.Calls);
  }

  [Fact]
  public async Task UpdateStep_WithNoGateWired_BehavesExactlyAsBefore()
  {
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()), () => "root-session-1");
    _ = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError, result.Content);
  }
}
