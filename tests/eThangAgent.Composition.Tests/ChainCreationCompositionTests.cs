using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #313 D6: the chain creation action lives in a NEW Chain capability
///     provider in the Agent Domain, and it is REACHABLE - a provider nobody can resolve is
///     a feature that does not exist. This pins the wiring a real session gets.</summary>
public class ChainCreationCompositionTests
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

  [Fact]
  public void ChainProvider_IsRegistered_AndExposesTheStartAction()
  {
    using ServiceProvider services = Build();

    ChainCapabilityProvider provider = services.GetRequiredService<ChainCapabilityProvider>();

    Assert.Equal("chain", provider.Id);
    Assert.Equal(["start"], provider.Actions.Select(a => a.Name));
  }

  /// <summary>The action is reachable through the SAME registry the exec door dispatches
  ///     through, which is the only thing that makes it callable by a model.</summary>
  [Fact]
  public void ChainStart_ResolvesThroughTheCapabilityRegistry()
  {
    using ServiceProvider services = Build();
    ICapabilityRegistry registry = services.GetRequiredService<ICapabilityRegistry>();

    Result<ResolvedCapability> resolved = registry.Resolve("chain.start");

    Assert.True(resolved.IsSuccess, resolved.Error?.Message ?? "chain.start must resolve");
    Assert.Equal("chain", resolved.Value.ProviderId);
  }

  /// <summary>A CHILD may start a chain: the action is not a session launch (it asks the
  ///     app for one through the port), and #316's grant surface is where a narrower rule
  ///     would be decided. Pinned so a later change is a deliberate one.</summary>
  [Fact]
  public void ChainStart_IsOnTheChildToolSurface()
  {
    using ServiceProvider services = Build();

    Assert.Contains("start", AgentComposition.ChildToolSurface(services));
  }

  [Fact]
  public void ChainCreationPorts_AreRegistered()
  {
    using ServiceProvider services = Build();

    Assert.NotNull(services.GetService<IChainPlanSource>());
    Assert.NotNull(services.GetService<IChainStartPort>());
  }
}
