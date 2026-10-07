using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The 'openrouter_management' tool rides the OpenRouter session's tool
///     surface (root and children) only when its ACL is wired; the ACL resolves
///     the management key from settings, and a session without one still builds -
///     the tool refuses at dispatch with ManagementUnavailable.</summary>
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
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
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

  [Fact]
  public void Management_Tool_Is_On_The_Loop_Registry()
  {
    using ServiceProvider services = Build("sk-or-mng-1");
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.NotNull(registry.Find("openrouter_management"));
  }

  [Fact]
  public async Task Tool_Without_Management_Key_Refuses_With_ManagementUnavailable()
  {
    using ServiceProvider services = Build(null);
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    ITool tool = registry.Find("openrouter_management")!;

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("openrouter_management", ListArgs),
        TestContext.Current.CancellationToken);

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
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    ITool tool = registry.Find("openrouter_management")!;

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("openrouter_management", ListArgs),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("ManagementError", result.Content, StringComparison.Ordinal);
  }
}
