using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Presentation.ACL;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The pptx tool is wired like docx and pdf: as a capability action (exec's
///     Tools.pptx) rather than a second top-level door, and an anchored provider re-roots
///     it like read. The ACL behind it is one singleton per container.</summary>
[Trait("Category", "Process")]
public class PptxCompositionTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    AppDatabase database = new(Path.Combine(TestTempRoot.Root, $"ethang-pptx-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("ws-pptx"), new UnrootedPathResolver()), database)
        .BuildServiceProvider();
  }

  /// <summary>exec is the only top-level tool, so 'pptx' is not on the loop registry - it
  ///     is reachable as Tools.pptx inside exec.</summary>
  [Fact]
  public void Loop_Registry_Does_Not_Carry_The_Pptx_Tool()
  {
    using ServiceProvider services = Build();
    IToolRegistry registry = services.GetRequiredService<IToolRegistry>();
    Assert.Null(registry.Find("pptx"));
  }

  [Fact]
  public void Capability_Surface_Carries_The_Pptx_Action()
  {
    using ServiceProvider services = Build();
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("pptx").IsSuccess);
  }

  /// <summary>The scoped-tool contract lives on the capability surface: re-rooting an
  ///     anchored child's exec surface is what makes Tools.pptx resolve at the anchor.</summary>
  [Fact]
  public void Pptx_Tool_IsWorkspaceScoped()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.RootedAt(TestTempRoot.Root).Actions, a => a.Name == "pptx");
  }

  /// <summary>One ACL instance per container: the tool is a thin seam over it, so two
  ///     instances would mean two independent views of the same deck's version.</summary>
  [Fact]
  public void Pptx_Access_Is_A_Singleton_Of_The_Direct_Acl()
  {
    using ServiceProvider services = Build();
    IPptxAccess first = services.GetRequiredService<IPptxAccess>();
    Assert.Same(first, services.GetRequiredService<IPptxAccess>());
    _ = Assert.IsType<DirectPptxAccess>(first);
  }

  /// <summary>The file-tool surface carries the document tools together, and the pptx
  ///     binding sits immediately after pdf so the model reads them as one family.</summary>
  [Fact]
  public void Pptx_Binding_Sits_Immediately_After_Pdf()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    IReadOnlyList<ActionDescriptor> actions = tools.RootedAt(TestTempRoot.Root).Actions;

    int pdf = IndexOf(actions, "pdf");
    Assert.True(pdf >= 0, "the pdf binding is missing from the file tool surface.");
    Assert.Equal("pptx", actions[pdf + 1].Name);
    Assert.Equal("Read, edit, create, and inspect .pptx presentations.", actions[pdf + 1].Summary);
  }

  /// <summary>The document family is present as a set: a session that can read a docx and
  ///     a pdf can read a deck.</summary>
  [Fact]
  public void File_Tool_Surface_Carries_The_Document_Family()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    IReadOnlyList<ActionDescriptor> actions = tools.RootedAt(TestTempRoot.Root).Actions;

    foreach (string name in new[] { "pptx", "docx", "pdf", "read", "write", "edit" })
    {
      Assert.True(IndexOf(actions, name) >= 0, $"'{name}' is missing from the file tool surface.");
    }
  }

  /// <summary>A re-rooted pptx tool reads and writes under the root it was composed for,
  ///     so an anchored child host cannot reach the parent's workspace through it.</summary>
  [Fact]
  public async Task Rooted_Pptx_Tool_Resolves_Under_The_Root_It_Was_Composed_For()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    string root = Path.Combine(TestTempRoot.Root, $"ws-pptx-anchored-{Guid.NewGuid():N}");
    _ = Directory.CreateDirectory(root);
    AgentToolsProvider anchored = tools.RootedAt(root);

    CapabilityInvocationResult created = await anchored.InvokeAsync(
        "pptx",
                             /*lang=json,strict*/
                             "{\"timeoutSeconds\":30,\"action\":\"create\",\"path\":\"deck.pptx\",\"markdown\":\"# One\\n- a\"}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(created.IsError, created.Content);
    Assert.True(File.Exists(Path.Combine(root, "deck.pptx")), "the deck was not written under the composed root.");

    CapabilityInvocationResult outline = await anchored.InvokeAsync(
        "pptx",
                             /*lang=json,strict*/
                             "{\"timeoutSeconds\":30,\"action\":\"read\",\"path\":\"deck.pptx\"}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(outline.IsError, outline.Content);
    Assert.Contains("One", outline.Content, StringComparison.Ordinal);

    AgentToolsProvider elsewhere = tools.RootedAt(Path.Combine(TestTempRoot.Root, "ws-pptx-other"));
    CapabilityInvocationResult missing = await elsewhere.InvokeAsync(
        "pptx",
                             /*lang=json,strict*/
                             "{\"timeoutSeconds\":30,\"action\":\"read\",\"path\":\"deck.pptx\"}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(missing.IsError);
    Assert.Contains("FileNotFound", missing.Content, StringComparison.Ordinal);
  }

  private static int IndexOf(IReadOnlyList<ActionDescriptor> actions, string name)
  {
    for (int i = 0; i < actions.Count; i++)
    {
      if (string.Equals(actions[i].Name, name, StringComparison.Ordinal))
      {
        return i;
      }
    }

    return -1;
  }
}
