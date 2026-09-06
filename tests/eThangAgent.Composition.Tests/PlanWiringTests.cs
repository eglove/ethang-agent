using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.PlanDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class PlanWiringTests
{
  // Fixture copied from ToolSurfaceTests.Build() — the established way to build the
  // composition's ServiceProvider in tests (FixedWorkspaceContext scopes the stores to "app").
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
  public void Container_ResolvesPlanProvider_WithSevenValidActions()
  {
    using ServiceProvider services = Build();
    PlanCapabilityProvider provider = services.GetRequiredService<PlanCapabilityProvider>();
    Assert.Equal("plan", provider.Id);
    Assert.Equal(7, provider.Actions.Count);
    Assert.All(provider.Actions, a => Assert.True(CapabilityNameRules.IsValidActionName(a.Name)));
  }

  [Fact]
  public void RootSurface_ResolvesPlanActions()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("plan.create").IsSuccess);
    Assert.True(surface().Resolve("plan.set-status").IsSuccess);
  }
}
