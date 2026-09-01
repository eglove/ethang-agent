using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>R1 spawner integration: the resolved effective set on the contract selects
///     the filtered registry; grants absent keeps the shared registry instance (zero
///     default-path delta); refusals land in the audit trail (R1.4).</summary>
public class SubAgentSpawnerGrantTests
{
  private static AgentRecord Child(string? contractJson, string prompt = "do things")
      => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", null, prompt,
          new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
          contractJson is null ? null : new SpawnContract(EffectiveTools: contractJson));

  private static (SubAgentSpawner Spawner, FakeAuditStore Audit) MakeRunner(
      FakeProvider provider, FakeAgentStore store, IToolRegistry tools)
  {
    FakeAuditStore audit = new();
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store, tools,
        new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m/sub"),
        Audit: audit));
    return (spawner, audit);
  }

  [Fact]
  public async Task RunAsync_NoGrantsInContract_UsesTheSharedRegistryInstance()
  {
    FakeAgentStore store = new();
    ToolRegistry shared = new([new FakeTool("read", "ok")]);
    (SubAgentSpawner spawner, FakeAuditStore _) = MakeRunner(
        new FakeProvider(Result.Success(new ModelResponse("done", []))), store, shared);

    _ = await spawner.RunAsync(Child(null), TestContext.Current.CancellationToken);

    // Zero behavior delta on the default path: the loop ran against the shared registry.
    // (The filtered view is only constructed when the contract carries a grant set.)
    FakeProvider probing = new(Result.Success(new ModelResponse(null,
        [new ToolCallRequest("c1", "read", "{}")])),
        Result.Success(new ModelResponse("ok", [])));
    (SubAgentSpawner prober, _) = MakeRunner(probing, store, shared);
    _ = await prober.RunAsync(Child(null, "call read"), TestContext.Current.CancellationToken);
    Assert.Contains(probing.RequestsSeen[1].Messages, m => m.Content == "ok");
  }

  [Fact]
  public async Task RunAsync_GrantedTool_Executes()
  {
    FakeAgentStore store = new();
    ToolRegistry shared = new([new FakeTool("web_fetch", "page"), new FakeTool("read", "file"), new FakeTool("exec", "ran")]);
    (SubAgentSpawner spawner, FakeAuditStore audit) = MakeRunner(
        new FakeProvider(
            Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "web_fetch", "{}")])),
            Result.Success(new ModelResponse("done", []))),
        store, shared);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child("read;web_fetch"), TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Assert.DoesNotContain(audit.Events, e => e.Kind == WatchdogEventKind.GrantViolation
        && e.Detail.Contains("'web_fetch'", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RunAsync_DeniedTool_RefusedWithContractLine_AndAudited()
  {
    FakeAgentStore store = new();
    ToolRegistry shared = new([new FakeTool("web_fetch", "page"), new FakeTool("read", "file"), new FakeTool("exec", "ran")]);
    (SubAgentSpawner spawner, FakeAuditStore audit) = MakeRunner(
        new FakeProvider(
            Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "exec", "{}")])),
            Result.Success(new ModelResponse("done", []))),
        store, shared);

    AgentRunOutcome outcome = await spawner.RunAsync(
        Child("web_fetch;read"), TestContext.Current.CancellationToken);

    // The run completes — a refusal is policy feedback, not a crash — and the denial
    // is audited as a GrantViolation row naming the tool.
    Assert.Equal(AgentStatus.Completed, outcome.Status);
    WatchdogEvent row = Assert.Single(audit.Events, e => e.Kind == WatchdogEventKind.GrantViolation);
    Assert.Contains("'exec'", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunAsync_DeniedTool_ConversationCarriesGrantViolationLine()
  {
    FakeAgentStore store = new();
    ToolRegistry shared = new([new FakeTool("exec", "ran")]);
    (SubAgentSpawner spawner, FakeAuditStore _) = MakeRunner(
        new FakeProvider(
            Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "exec", "{}")])),
            Result.Success(new ModelResponse("done", []))),
        store, shared);

    _ = await spawner.RunAsync(Child("read"), TestContext.Current.CancellationToken);

    Result<IReadOnlyList<ConversationDomain.Message>> transcript =
        await store.GetTranscriptAsync(store.Updated.Single().Id, ct: TestContext.Current.CancellationToken);
    Assert.True(transcript.IsSuccess);
    Assert.Contains(transcript.Value, m =>
        m.Content == "Error [GrantViolation]: tool 'exec' is not granted to this agent.");
  }
}
