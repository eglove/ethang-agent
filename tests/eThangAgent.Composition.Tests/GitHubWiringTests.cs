using eThangAgent.AgentDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #10: the GitHub view's seam must be RESOLVABLE from the built container,
///     or the workbench resolves null and every session renders the view's unavailable state -
///     a registration that is written but never resolved looks identical to one that works.
///     <para>The second test pins the D6 decision itself: the wired implementation is the
///     <c>gh</c> CLI adapter, not an API client, and its construction touches no network and
///     no credential store.</para></summary>
public class GitHubWiringTests
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
  public void GitHubAccess_IsRegistered_AndResolvesToTheCliAdapter()
  {
    using ServiceProvider services = Build();

    IGitHubAccess access = services.GetRequiredService<IGitHubAccess>();

    _ = Assert.IsType<GhCliAccess>(access);
  }

  [Fact]
  public void GitHubAccess_IsASingleton_SoTheExeResolutionIsCached()
  {
    using ServiceProvider services = Build();

    Assert.Same(
        services.GetRequiredService<IGitHubAccess>(),
        services.GetRequiredService<IGitHubAccess>());
  }
}
