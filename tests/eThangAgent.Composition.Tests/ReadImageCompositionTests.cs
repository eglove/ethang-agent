using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The read_image tool is wired twice (issue #20): as a capability action
///     (exec's Tools.read_image) and on the loop registry (direct dispatch, so the
///     tool result's image part enters the conversation). Anchored children re-root
///     it like read.</summary>
public class ReadImageCompositionTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-readimg-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("ws-readimg"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  /// <summary>Issue #288: exec is the only top-level tool, so 'read_image' is NOT on the
  ///     loop registry. It stays reachable as Tools.read_image inside exec, and the image
  ///     it attaches rides the exec result (pinned end to end by ExecImageChannelTests).</summary>
  [Fact]
  public void Loop_Registry_Does_Not_Carry_The_ReadImage_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.Null(registry.Find("read_image"));
  }

  [Fact]
  public void Capability_Surface_Carries_The_ReadImage_Action()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("read_image").IsSuccess);
  }

  /// <summary>The scoped-tool contract lives on the capability surface now: re-rooting
  ///     an anchored child's exec surface is what makes Tools.read_image resolve at the anchor.</summary>
  [Fact]
  public void ReadImage_Tool_IsWorkspaceScoped()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.RootedAt(Path.GetTempPath()).Actions, a => a.Name == "read_image");
  }
}
