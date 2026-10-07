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
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws-readimg"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Loop_Registry_Carries_The_ReadImage_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    ReadImageTool tool = Assert.IsType<ReadImageTool>(registry.Find("read_image")!);
    Assert.Equal("read_image", tool.Definition.Name);
  }

  [Fact]
  public void Capability_Surface_Carries_The_ReadImage_Action()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("read_image").IsSuccess);
  }

  [Fact]
  public void ReadImage_Tool_IsWorkspaceScoped()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.True(registry.Find("read_image") is IWorkspaceScopedTool);
  }
}
