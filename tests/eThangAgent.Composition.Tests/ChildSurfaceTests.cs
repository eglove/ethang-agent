using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The capability-surface split: every agent tool is SelfManaged (the tool
///     contract owns its timeoutSeconds budget), and human-facing actions — clarify,
///     the only user-facing tool — exist on the root surface only. Sub-agents resolve
///     their own registry through the exec engine's per-execution resolver.</summary>
public class ChildSurfaceTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new("sk-or-test", new Uri("https://openrouter.test"),
        new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, settings.ApiKey!,
            ModelConfig.Create("test/model", null, 512, 0.5f).Value!,
            new AgentHostOptions(new SilentClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  /// <summary>A channel that never answers: these tests only resolve surfaces,
  ///     they never ask the human anything.</summary>
  private sealed class SilentClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => throw new NotSupportedException("No test should reach the human.");
  }

  [Fact]
  public void Every_AgentTool_Action_Is_SelfManaged()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.NotEmpty(tools.Actions);
    Assert.All(tools.Actions, a => Assert.Equal(TimeoutPolicy.SelfManaged, a.Timeout));
  }

  [Fact]
  public void Root_Surface_Resolves_Clarify_And_Child_Filter_Removes_It()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    ICapabilityRegistry root = surface();
    Assert.True(root.Resolve("clarify").IsSuccess);

    AgentToolsProvider childTools = tools.Except("clarify");
    Assert.DoesNotContain(childTools.Actions, a => a.Name == "clarify");
    Assert.Equal(tools.Actions.Count - 1, childTools.Actions.Count);
  }

  [Fact]
  public void Except_Unknown_Action_Fails_Loudly()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    _ = Assert.Throws<ArgumentException>(() => tools.Except("clarify_typo"));
  }
}
