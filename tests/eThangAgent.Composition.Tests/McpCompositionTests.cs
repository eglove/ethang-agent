using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>MCP composition tests (issue #104): the mcp tool joins the loop registry
///     and the capability surface, the definition budget stays FLAT regardless of
///     configured server count (B1), and the per-workspace access resolves through
///     the provider seam.</summary>
public class McpCompositionTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-mcpcomp-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws-mcp"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Loop_Registry_Carries_The_Mcp_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.NotNull(registry.Find("mcp"));
  }

  [Fact]
  public async Task Definition_Count_Is_Flat_With_Ten_Configured_Servers()
  {
    // B1: the bootstrap's definition count is identical with zero and with ten
    // configured servers. The mcp tool is ONE definition either way; servers are
    // discovered through the listing action, never upfront definitions.
    using ServiceProvider zero = Build();
    IToolRegistry zeroRegistry = zero.GetRequiredService<IToolRegistry>();
    int zeroCount = zeroRegistry.Definitions.Count;

    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-mcpcomp-{Guid.NewGuid():N}.db"));
    try
    {
      SqliteMcpServerStore store = new(database);
      for (int i = 0; i < 10; i++)
      {
        _ = await store.AddAsync(new ToolDomain.Mcp.McpServerConfig(0, $"server{i}",
            ToolDomain.Mcp.McpTransport.Stdio, "npx", "[]", "{}", "{}", null,
            ToolDomain.Mcp.McpApprovalState.Approved, null, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      using ServiceProvider ten = BuildWithStore(store);
      IToolRegistry tenRegistry = ten.GetRequiredService<IToolRegistry>();
      Assert.Equal(zeroCount, tenRegistry.Definitions.Count);
      Assert.True(zeroCount > 0);
    }
    finally
    {
      try
      {
        File.Delete(database.DatabasePath);
      }
      catch (IOException)
      {
        // Named decision: the pooled connection may still hold the file; temp cleanup is best effort.
      }
    }
  }

  [Fact]
  public void Capability_Surface_Resolves_Mcp()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("mcp").IsSuccess);
  }

  [Fact]
  public void Container_Registers_One_Shared_Mcp_Server_Access()
  {
    // Issue #106: the status view reads the SAME pooled access the mcp tool
    // dispatches through - one IMcpServerAccess per container, so the dialog's
    // live state (connected/failed) is the session's own pool state.
    using ServiceProvider services = Build();
    ToolDomain.Mcp.IMcpServerAccess access = services.GetRequiredService<ToolDomain.Mcp.IMcpServerAccess>();
    _ = Assert.IsType<ToolDomain.Mcp.McpServerAccess>(access);

    // The loop tool and the capability surface share it: both bindings resolve
    // the same singleton, never two pools over one workspace.
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.NotNull(registry.Find("mcp"));
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("mcp").IsSuccess);
  }

  [Fact]
  public void Workspace_Access_Resolves_Per_Workspace()
  {
    using ServiceProvider services = Build();
    ToolDomain.Mcp.IMcpServerAccessProvider provider =
        services.GetRequiredService<ToolDomain.Mcp.IMcpServerAccessProvider>();
    ToolDomain.Mcp.IMcpServerAccess access = provider.ForWorkspace("ws-mcp");
    _ = Assert.IsType<ToolDomain.Mcp.McpServerAccess>(access);
  }

  private static ServiceProvider BuildWithStore(SqliteMcpServerStore store)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-mcpcomp-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws-mcp"), new UnrootedPathResolver()), database)
        .AddSingleton<ToolDomain.Mcp.IMcpServerStore>(store)
        .BuildServiceProvider();
  }

  // JSON002 fires only in the format/IDE host; the pragma pair is the repo's
  // named decision for hand-written JSON test shapes.
#pragma warning disable JSON002 // Probable JSON string detected
  private const string GrantProbeArgs = "{\"timeoutSeconds\":120,\"action\":\"call\",\"server\":\"gitlab\",\"tool\":\"push\"}";
#pragma warning restore JSON002 // Probable JSON string detected

  [Fact]
  public async Task Loop_Mcp_Tool_Reads_The_Ambient_Grant_Scope()
  {
    // Issue #108: the composition's mcp tool is built with the ambient grant
    // scope, so a scoped child's dispatches re-check resolved ids. The probe:
    // set the ambient, dispatch through the tool, observe the refusal.
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    ToolDomain.Mcp.McpTool tool = Assert.IsType<ToolDomain.Mcp.McpTool>(registry.Find("mcp"));
    ToolDomain.Mcp.AmbientMcpGrantScope.Current = new ToolDomain.Mcp.McpGrantScope(
        new HashSet<string>(StringComparer.Ordinal) { "mcp", "mcp.github.*" });
    try
    {
      ToolResult result = await tool.ExecuteAsync(new RawToolInput("mcp", GrantProbeArgs),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(result.IsError);
      Assert.Equal("Error [GrantViolation]: tool 'mcp.gitlab.push' is not granted to this agent.", result.Content);
    }
    finally
    {
      ToolDomain.Mcp.AmbientMcpGrantScope.Current = null;
    }
  }
}
