
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #124 defect 1b (2026-10-15 incident, child 7c97d74d): a begin-run that
///     reaches the loop without a provider binding must fail LOUDLY as a well-formed
///     Failed outcome with an audit row — never loop on tools with zero model calls.
///     The invariant engages only on production wiring (SubAgentServices.ProviderRequired
///     = true); legacy test wiring defaults false and stays byte-identical.</summary>
public class SubAgentSpawnerProviderInvariantTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static AgentRecord Child(string? provider) => new(
      AgentId.NewId(), null, 1, AgentStatus.Running, null, "default/sub-model", null,
      "do things", FixedNow, null, null, Provider: provider);

  [Fact]
  public async Task RunAsync_UnboundProvider_FailsLoudly_WithAuditRow()
  {
    FakeAgentStore store = new();
    FakeAuditStore audit = new();
    SubAgentServices services = new(
        new FakeModelProviderFactory(new FakeProvider()),
        store, new ToolRegistry([]), new StaticPromptProvider("guide text"),
        new SubAgentOptions(DefaultModel: "default/sub-model"),
        Audit: audit, ProviderRequired: true);
    SubAgentSpawner spawner = new(services);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(provider: AgentRecord.UnboundProvider), CancellationToken.None)
        .ConfigureAwait(true);

    Assert.Equal(AgentStatus.Failed, outcome.Status);
    Assert.Equal(AgentFailureReason.ProviderError, outcome.Reason);
    Assert.Contains("provider binding", outcome.Report, StringComparison.Ordinal);
    // Terminal row persisted — no phantom Running survives the refused run.
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal(AgentStatus.Failed, updated.Status);
    Assert.Equal(AgentFailureReason.ProviderError, updated.FailureReason);
    // The decision lands in the watchdog event store as audit.
    _ = Assert.Single(audit.Events, e => e.AgentId == outcome.ChildId);
  }

  [Fact]
  public async Task RunAsync_BoundProvider_RunsNormally()
  {
    FakeAgentStore store = new();
    FakeAuditStore audit = new();
    SubAgentServices services = new(
        new FakeModelProviderFactory(new FakeProvider(Result.Success(new ModelResponse("ok", [])))),
        store, new ToolRegistry([]), new StaticPromptProvider("guide text"),
        new SubAgentOptions(DefaultModel: "default/sub-model"),
        Audit: audit, ProviderRequired: true);
    SubAgentSpawner spawner = new(services);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(provider: "openrouter"), CancellationToken.None)
        .ConfigureAwait(true);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.Empty(audit.Events);
  }

  [Fact]
  public async Task RunAsync_RequirementWiredOff_UnboundChildKeepsLegacyBehavior()
  {
    // Legacy wiring (tests, older hosts): no requirement, no invariant — the child runs
    // byte-identically to before this change.
    FakeAgentStore store = new();
    SubAgentServices services = new(
        new FakeModelProviderFactory(new FakeProvider(Result.Success(new ModelResponse("ok", [])))),
        store, new ToolRegistry([]), new StaticPromptProvider("guide text"),
        new SubAgentOptions(DefaultModel: "default/sub-model"));
    SubAgentSpawner spawner = new(services);

    AgentRunOutcome outcome = await spawner.RunAsync(Child(provider: AgentRecord.UnboundProvider), CancellationToken.None)
        .ConfigureAwait(true);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
  }
}
