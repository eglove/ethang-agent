using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The context-trim wiring contract: ONE context tool carries List, Remove,
///     Shorten, and Trim, and it is on the agent tool surface (root and children
///     alike). The adapter delegates every action to the wrapped Conversation Domain
///     service.</summary>
[Trait("Category", "Process")]
public class ContextTrimWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void ContextTool_IsOnAgentToolSurface()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.Contains(tools.Actions, a => a.Name == "context_edit");
  }

  [Fact]
  public void ContextTrimTool_IsGone_ItsActionsLiveOnContextEdit()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.DoesNotContain(tools.Actions, a => a.Name == "context_trim");
  }

  [Fact]
  public void ContextTool_IsResolvable_OnChildSurface()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();

    Assert.True(surface().Resolve("context_edit").IsSuccess);
  }
}
