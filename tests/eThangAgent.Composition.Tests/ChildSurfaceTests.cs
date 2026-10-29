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

  /// <summary>GRANT DECISION (issue #234): a spawned child may NOT start sessions — a
  /// child has no workspace identity of its own and a session launch is a shell-level
  /// act. The tool stays root-only; a child's dispatch fails GrantViolation via the
  /// filtered registry. The surface under test is the one the spawn path grants from
  /// (SpawnOptions.ChildToolSurface), not the root capability registry.</summary>
  [Fact]
  public void NewSession_Tool_Is_Excluded_From_The_Child_Surface()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "new_session"); // bound at the root surface

    HashSet<string> childSurface = AgentComposition.ChildToolSurface(services);
    Assert.Contains("read", childSurface); // the surface is not empty
    Assert.DoesNotContain("new_session", childSurface);
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
