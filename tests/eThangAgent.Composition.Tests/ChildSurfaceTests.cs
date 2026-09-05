using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The capability surface: every agent tool is SelfManaged (the tool
///     contract owns its timeoutSeconds budget). Sub-agents resolve their own
///     registry through the exec engine's per-execution resolver.</summary>
public class ChildSurfaceTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new ZaiSettings(null, new Uri("https://zai.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  [Fact]
  public void Every_AgentTool_Action_Is_SelfManaged()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.NotEmpty(tools.Actions);
    Assert.All(tools.Actions, a => Assert.Equal(TimeoutPolicy.SelfManaged, a.Timeout));
  }
}
