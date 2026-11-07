using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Frontends may append their own prompt providers (e.g. workspace instructions)
/// via AgentHostOptions; the composition must merge them into the composite system prompt.</summary>
public class HostPromptProviderCompositionTests
{
  private static ServiceProvider BuildCore(AgentHostOptions host)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!, host, db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Extra_Providers_Are_Merged_Into_The_Composite_System_Prompt()
  {
    using ServiceProvider services = BuildCore(new AgentHostOptions(
        FixedWorkspaceContext.ForPath("app"),
        new UnrootedPathResolver(),
        [new StaticPromptProvider("EXTRA-PROMPT-MARKER-123")]));

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    Assert.Contains("EXTRA-PROMPT-MARKER-123", prompt, StringComparison.Ordinal);
  }

  [Fact]
  public void Core_Providers_Precede_Frontend_Providers()
  {
    using ServiceProvider services = BuildCore(new AgentHostOptions(
        FixedWorkspaceContext.ForPath("app"),
        new UnrootedPathResolver(),
        [new StaticPromptProvider("FRONTEND-TAIL-MARKER")]));

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    int core = prompt.IndexOf("eThang Agent", StringComparison.Ordinal);
    int tail = prompt.IndexOf("FRONTEND-TAIL-MARKER", StringComparison.Ordinal);
    Assert.True(core >= 0 && tail > core,
        $"core={core} tail={tail}: frontend provider must come after the core identity prompt");
  }

  // The prompt a user actually receives is THREE sections from two sources: the
  // harness's built-in bootstrap, then the global session file, then the workspace
  // session file. Each must be delimited and name its own source, in that order, or a
  // file's own H1 reads as the title of the section above it.
  [Fact]
  public void Composite_Renders_Three_Delimited_Sections_In_Source_Order()
  {
    string dir = TestTempRoot.CreateDirectory("ethang-sections").FullName;
    try
    {
      string globalPath = Path.Combine(dir, "global.md");
      string workspacePath = Path.Combine(dir, "workspace.md");
      File.WriteAllText(globalPath, "GLOBAL-SECTION-BODY");
      File.WriteAllText(workspacePath, "WORKSPACE-SECTION-BODY");

      using ServiceProvider services = BuildCore(new AgentHostOptions(
          FixedWorkspaceContext.ForPath(dir),
          new UnrootedPathResolver(),
          [new SessionFilesPromptProvider(dir,
              SessionFilePreferences.Serialize([new SessionFileEntry(globalPath, true)]),
              SessionFilePreferences.Serialize([new SessionFileEntry(workspacePath, true)]))]));

      string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

      int bootstrap = prompt.IndexOf("<harness-bootstrap", StringComparison.Ordinal);
      int global = prompt.IndexOf(
          "<session-file scope=\"global\" path=\"" + Path.GetFullPath(globalPath) + "\">",
          StringComparison.Ordinal);
      int workspace = prompt.IndexOf(
          "<session-file scope=\"workspace\" path=\"" + Path.GetFullPath(workspacePath) + "\">",
          StringComparison.Ordinal);

      Assert.True(bootstrap >= 0, "the harness bootstrap section must be delimited");
      Assert.True(global > bootstrap, $"global file ({global}) must follow the bootstrap ({bootstrap})");
      Assert.True(workspace > global, $"workspace file ({workspace}) must follow the global file ({global})");
    }
    finally
    {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Fact]
  public void Default_Options_Carry_No_Extra_Providers()
  {
    AgentHostOptions options = new(
        FixedWorkspaceContext.ForPath("app"),
        new UnrootedPathResolver());

    Assert.Empty(options.ExtraPromptProviders);
  }
}
