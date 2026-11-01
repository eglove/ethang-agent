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
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>The surface under test is the one the spawn path grants from
  ///     (SpawnOptions.ChildToolSurface), not the root capability registry.</summary>
  [Fact]
  public void Child_Surface_Is_Not_Empty()
  {
    using ServiceProvider services = Build();

    HashSet<string> childSurface = AgentComposition.ChildToolSurface(services);
    Assert.Contains("read", childSurface);
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
