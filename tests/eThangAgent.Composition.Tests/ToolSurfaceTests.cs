using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Surface contract after the clarify removal (grand plan: the LLM
///     formats its own questions): no tool asks the human a question mid-turn.
///     The root surface must not resolve a clarify action, and no agent tool
///     binding may carry that name.</summary>
public class ToolSurfaceTests
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
  public void No_AgentToolBinding_Is_Named_Clarify()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    Assert.DoesNotContain(tools.Actions, a => a.Name == "clarify");
  }

  [Fact]
  public void Root_Surface_Does_Not_Resolve_Clarify()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();

    Assert.False(surface().Resolve("clarify").IsSuccess);
  }
}
