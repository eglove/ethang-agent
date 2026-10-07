using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The context-edit wiring contract: the context_edit tool is on the
///     agent tool surface (root and children alike), and the root agent's loop
///     carries a conversation-backed context service so milestone shrinks and
///     fine-grained edits act on the SHARED conversation. Grand-plan: agent-
///     triggered compaction plus fine-grained context edits.</summary>
public class ContextEditWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void ContextEditTool_IsOnAgentToolSurface()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.Contains(tools.Actions, a => a.Name == "context_edit");
  }

  [Fact]
  public void ContextEditTool_IsResolvable_OnChildSurface()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();

    Assert.True(surface().Resolve("context_edit").IsSuccess);
  }
}
