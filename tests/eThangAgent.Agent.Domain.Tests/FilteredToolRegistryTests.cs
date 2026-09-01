using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>R1 unit tests: the filtered registry exposes only granted tools (R1.1),
///     distinguishes denied-but-real from unknown (R1.3), and fires the audit callback
///     on every denial (R1.4).</summary>
public class FilteredToolRegistryTests
{
  private static readonly string[] AllNames = ["web_fetch", "read", "exec"];

  private static ToolRegistry Inner() => new([.. AllNames.Select(n => new FakeTool(n, "ok"))]);

  private static HashSet<string> Set(params string[] names) => [.. names];

  [Fact]
  public void Definitions_ContainsOnlyGrantedTools()
  {
    FilteredToolRegistry registry = new(Inner(), Set("web_fetch", "read"));

    string[] names = [.. registry.Definitions.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal)];
    Assert.Equal(["read", "web_fetch"], names);
  }

  [Fact]
  public void Find_GrantedTool_ReturnsTool()
  {
    FilteredToolRegistry registry = new(Inner(), Set("read"));

    Assert.NotNull(registry.Find("read"));
  }

  [Fact]
  public void Find_DeniedButRealTool_ReturnsNull()
  {
    FilteredToolRegistry registry = new(Inner(), Set("read"));

    Assert.Null(registry.Find("exec"));
  }

  [Fact]
  public void Find_UnknownTool_ReturnsNull()
  {
    FilteredToolRegistry registry = new(Inner(), Set("read"));

    Assert.Null(registry.Find("no_such_tool"));
  }

  [Fact]
  public void ExplainsRefusal_DeniedButReal_ProducesVerbatimContract()
  {
    FilteredToolRegistry registry = new(Inner(), Set("read"));

    string? refusal = registry.ExplainsRefusal("exec");
    Assert.Equal("Error [GrantViolation]: tool 'exec' is not granted to this agent.", refusal);
  }

  [Fact]
  public void ExplainsRefusal_UnknownName_Null_PolicyNeverMasksTypo()
  {
    FilteredToolRegistry registry = new(Inner(), Set("read"));

    Assert.Null(registry.ExplainsRefusal("no_such_tool"));
  }

  [Fact]
  public void Find_DeniedDispatch_FiresAuditCallback_OncePerDenial()
  {
    List<string> denied = [];
    FilteredToolRegistry registry = new(Inner(), Set("read"), onDenial: denied.Add);

    _ = registry.Find("exec");
    _ = registry.Find("web_fetch");
    _ = registry.Find("read");          // granted: no audit
    _ = registry.Find("no_such_tool"); // unknown: no audit

    Assert.Equal(["exec", "web_fetch"], denied);
  }

  [Fact]
  public async Task AgentLoop_DeniedDispatch_ReceivesStructuredRefusal_NotUnknownTool()
  {
    // Full-loop contract (R1.3): a child whose registry is the filtered view gets the
    // verbatim GrantViolation line appended to its conversation, not UnknownTool.
    FakeAgentStore store = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "exec", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store,
        new FilteredToolRegistry(Inner(), Set("read")),
        new StaticPromptProvider("guide"), new SubAgentOptions(DefaultModel: "m/sub")));
    AgentRecord child = AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub", null,
        "use exec", DateTimeOffset.UtcNow,
        new SpawnContract(EffectiveTools: "read"));

    AgentRunOutcome outcome = await spawner.RunAsync(child, TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    Result<IReadOnlyList<ConversationDomain.Message>> transcript =
        await store.GetTranscriptAsync(child.Id, ct: TestContext.Current.CancellationToken);
    Assert.True(transcript.IsSuccess);
    ConversationDomain.Message toolResult = Assert.Single(
        transcript.Value, m => m.Role == ConversationDomain.Role.User && m.Content.StartsWith("Error [GrantViolation]", StringComparison.Ordinal));
    Assert.Equal("Error [GrantViolation]: tool 'exec' is not granted to this agent.", toolResult.Content);
  }
}
