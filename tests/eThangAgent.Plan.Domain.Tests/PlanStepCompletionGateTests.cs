using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.PlanDomain.Tests;

/// <summary>Issue #304 at the STEP level: a step claiming Done is a completion claim, so it
///     asks the SAME gate a plan completion asks (D1). The gate is faked here - the real
///     evaluator is covered by the Tool Domain's own tests and the Composition's wiring test.
///     </summary>
public class PlanStepCompletionGateTests
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
        Task.FromResult(Result.Success(new PlanCatalog([.. _plans.Values], [])));

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

  private sealed class CountingGate(bool allowed, string message) : IPlanCompletionGate
  {
    public int Calls { get; private set; }

    public Task<(bool Allowed, string Message)> EvaluateAsync(CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult((allowed, message));
    }
  }

  private static async Task<(PlanCapabilityProvider Provider, CountingGate Gate)> SeededAsync(
      bool allowed)
  {
    CountingGate gate = new(allowed, "[completion gate] the gate is stale; run state.checkgoal");
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()), () => "s1", gate);
    CapabilityInvocationResult created = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError);
    return (provider, gate);
  }

  // THE ACCEPTANCE CRITERION: a step cannot reach Done without a fresh passing gate.
  [Fact]
  public async Task Done_WithStaleGate_FailsWithTheTypedCode()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(allowed: false);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("CompletionGateNotFresh", result.Content, StringComparison.Ordinal);
    Assert.Contains("state.checkgoal", result.Content, StringComparison.Ordinal);
  }

  // D3: the STEP refusal names the step's own exit, which the gate's plan-level message cannot
  // know about. Without it the cheapest way past the refusal would be a false Done.
  [Fact]
  public async Task Done_WithStaleGate_NamesSkippedAsTheStepExit()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(allowed: false);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Contains("Skipped", result.Content, StringComparison.Ordinal);
  }

  // A refusal must leave the plan untouched - a gate that half-applied is worse than none.
  [Fact]
  public async Task Done_WithStaleGate_LeavesTheStepPending()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(allowed: false);
    _ = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    CapabilityInvocationResult shown = await provider.InvokeAsync("show",
        /*lang=json,strict*/"{ \"id\": 1 }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Contains("[ Pending ]", shown.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Done_WithFreshGate_Succeeds()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(allowed: true);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("updated step 1", result.Content, StringComparison.Ordinal);
  }

  // SKIPPED IS NEVER GATED: it is the truthful exit the completion invariant depends on, so
  // gating it would make the honest answer unreachable.
  [Fact]
  public async Task Skipped_WithStaleGate_StillSucceeds()
  {
    (PlanCapabilityProvider provider, _) = await SeededAsync(allowed: false);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Skipped\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
  }

  // The session's own bookkeeping is never a completion claim, so it never asks the gate.
  [Theory]
  [InlineData("InProgress")]
  [InlineData("Pending")]
  public async Task NonCompletionStatuses_NeverConsultTheGate(string status)
  {
    (PlanCapabilityProvider provider, CountingGate gate) = await SeededAsync(allowed: false);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"" + status + "\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal(0, gate.Calls);
  }

  // An unset gate never blocks (D2): a provider with no gate wired behaves exactly as before.
  [Fact]
  public async Task NoGateWired_DoneSucceeds()
  {
    PlanCapabilityProvider provider = new(new PlanService(new FakeStore()), () => "s1");
    _ = await provider.InvokeAsync("create",
        /*lang=json,strict*/"{ \"title\": \"Ship\", \"goal\": \"G\", \"steps\": [ { \"title\": \"one\" } ] }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    CapabilityInvocationResult result = await provider.InvokeAsync("update-step",
        /*lang=json,strict*/"{ \"id\": 1, \"position\": 1, \"status\": \"Done\" }",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
  }
}
