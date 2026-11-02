using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>A MODEL MUST BE ABLE TO REACH agent.launch FROM ITS OWN SCRIPT (issue #338): the
///     whole point of the action is the model's own path into starting an agent, so a launch
///     that resolved only on the root surface would be a capability the model cannot see.
///     <para>These read the real composed surface rather than the provider's literal list, which
///     is what makes them prove reachability instead of restating a declaration.</para></summary>
public class AgentLaunchCompositionTests
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

  /// <summary>The child's grant surface includes the launch action: a narrowed child must be
  ///     able to reach it, or the model's own exec script resolves UnknownAction.</summary>
  [Fact]
  public void Launch_IsOnTheChildToolSurface()
  {
    using ServiceProvider services = Build();

    HashSet<string> surface = AgentComposition.ChildToolSurface(services);

    Assert.Contains("launch", surface);
  }

  /// <summary>And the composed registry RESOLVES it, by bare name and by its dotted provider
  ///     reference - the two ways a script names an action.</summary>
  [Fact]
  public void Launch_ResolvesByName_OnTheComposedRegistry()
  {
    using ServiceProvider services = Build();
    ICapabilityRegistry registry = services.GetRequiredService<Func<ICapabilityRegistry>>()();

    Assert.True(registry.Resolve("launch").IsSuccess,
        registry.Resolve("launch").Error?.Message ?? "agent.launch must resolve");
    Assert.True(registry.Resolve("agent.launch").IsSuccess,
        registry.Resolve("agent.launch").Error?.Message ?? "agent.launch must resolve");
  }

  /// <summary>THE FLAT-NAME INVARIANT: launch did not take a name another provider owns, and no
  ///     provider took its. A collision here throws at registry construction, taking the session
  ///     down - which is why the action is 'launch' rather than 'start'.</summary>
  [Fact]
  public void Launch_DoesNotCollideWithAnyOtherProvidersActionName()
  {
    using ServiceProvider services = Build();
    ICapabilityRegistry registry = services.GetRequiredService<Func<ICapabilityRegistry>>()();

    string[] duplicates = [.. registry.Providers
        .SelectMany(p => p.Actions)
        .GroupBy(a => a.Name, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)];

    Assert.True(duplicates.Length == 0,
        "duplicate action names across providers: " + string.Join(", ", duplicates));
    _ = Assert.Single(registry.Providers.SelectMany(p => p.Actions), a => a.Name == "launch");
  }

  /// <summary>The port is wired, so a launched agent is possible at all in a production-shaped
  ///     container; and the provider holds it.</summary>
  [Fact]
  public void LaunchPort_IsWired()
  {
    using ServiceProvider services = Build();

    Assert.NotNull(services.GetService<IAgentLaunchCommand>());
  }
}
