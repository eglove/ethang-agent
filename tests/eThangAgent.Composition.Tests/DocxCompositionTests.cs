using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The docx tool is wired twice like read_image (the plan's Task 12): as a
///     capability action (exec's Tools.docx) and on the loop registry; anchored
///     children re-root it like read.</summary>
public class DocxCompositionTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-docx-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws-docx"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Loop_Registry_Carries_The_Docx_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    DocxTool tool = Assert.IsType<DocxTool>(registry.Find("docx")!);
    Assert.Equal("docx", tool.Definition.Name);
  }

  [Fact]
  public void Capability_Surface_Carries_The_Docx_Action()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("docx").IsSuccess);
  }

  [Fact]
  public void Docx_Tool_IsWorkspaceScoped()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.True(registry.Find("docx") is IWorkspaceScopedTool);
  }
}
