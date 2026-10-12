using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The pdf tool is wired twice like docx (the plan's Task 9): as a
///     capability action (exec's Tools.pdf) and on the loop registry; anchored
///     children re-root it like read.</summary>
public class PdfCompositionTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-pdf-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws-pdf"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Loop_Registry_Carries_The_Pdf_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    PdfTool tool = Assert.IsType<PdfTool>(registry.Find("pdf")!);
    Assert.Equal("pdf", tool.Definition.Name);
  }

  [Fact]
  public void Capability_Surface_Carries_The_Pdf_Action()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("pdf").IsSuccess);
  }

  [Fact]
  public void Pdf_Tool_IsWorkspaceScoped()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.True(registry.Find("pdf") is IWorkspaceScopedTool);
  }
}
