using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The 'openrouter_management' tool rides the agent's capability surface (root
///     and children) only when its ACL is wired; the ACL resolves the management key from
///     settings, and a session without one still builds - the tool refuses at dispatch
///     with ManagementUnavailable. Since issue #288 that surface is reached ONLY from
///     inside exec (Tools.openrouter_management), so the tests dispatch through the
///     capability registry rather than a loop tool.</summary>
public class OpenRouterManagementWiringTests
{
  private static ServiceProvider Build(string? managementKey)
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test"), ManagementKey: managementKey),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  private const string ListArgs =
      /*lang=json,strict*/ """{"timeoutSeconds":30,"action":"credits"}""";

  [Fact]
  public void Management_Tool_Is_On_The_Agent_Surface()
  {
    using ServiceProvider services = Build("sk-or-mng-1");
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "openrouter_management");
  }

  /// <summary>Issue #288: exec is the only top-level tool, so 'openrouter_management' is
  ///     reached as Tools.openrouter_management inside exec and is NOT on the loop registry.</summary>
  [Fact]
  public void Management_Tool_Is_Not_On_The_Loop_Registry()
  {
    using ServiceProvider services = Build("sk-or-mng-1");
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.Null(registry.Find("openrouter_management"));
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("openrouter_management").IsSuccess);
  }

  [Fact]
  public async Task Tool_Without_Management_Key_Refuses_With_ManagementUnavailable()
  {
    using ServiceProvider services = Build(null);

    CapabilityInvocationResult result = await DispatchAsync(services, ListArgs).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ManagementUnavailable", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Tool_With_Management_Key_Dispatches_Over_The_Wired_Client()
  {
    // No live HTTP here: the wired client is real, so a dispatch against the fake
    // base URL fails at transport - the assertion is a ManagementError (the client
    // ran), never MissingParameter or a crash.
    using ServiceProvider services = Build("sk-or-mng-1");

    CapabilityInvocationResult result = await DispatchAsync(services, ListArgs).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ManagementError", result.Content, StringComparison.Ordinal);
  }

  /// <summary>One dispatch exactly as a script makes it (Tools.openrouter_management).</summary>
  private static Task<CapabilityInvocationResult> DispatchAsync(ServiceProvider services, string args)
  {
    ICapabilityRegistry surface = services.GetRequiredService<Func<ICapabilityRegistry>>()();
    ResolvedCapability resolved = surface.Resolve("openrouter_management").Value
        ?? throw new InvalidOperationException("the capability surface does not carry 'openrouter_management'");
    return surface.InvokeAsync(resolved, args, TestContext.Current.CancellationToken);
  }
}
