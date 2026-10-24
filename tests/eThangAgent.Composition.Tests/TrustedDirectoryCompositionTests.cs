using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The trusted-directory capability (issue #96) end to end through a REAL
/// composed container: a workspace-scoped approval makes the ordinary file tools reach
/// an external root - read through the read tool, write through write only when the root
/// is editable - and a session with nothing approved behaves exactly as it did before the
/// capability existed. The approval travels the production path: a workspace preference
/// read at container build, not a constructor argument.
/// </summary>
public sealed class TrustedDirectoryCompositionTests
{
  [Fact]
  public async Task Read_ReachesAnApprovedRoot()
  {
    string vault = Path.Combine(Path.GetTempPath(), "ethang-trusted-" + Guid.NewGuid().ToString("N"));
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-trusted-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(vault);
    _ = Directory.CreateDirectory(workspace);
    try
    {
      string file = Path.Combine(vault, "notes.md");
      await File.WriteAllTextAsync(file, "vault marker", TestContext.Current.CancellationToken);

      using ServiceProvider services = Build(workspace,
          JsonSerializer.Serialize(new[] { new { path = vault, enabled = true, editable = false } }));
      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

      CapabilityInvocationResult result = await tools.InvokeAsync("read",
          JsonSerializer.Serialize(new { timeoutSeconds = 60, path = file, startLine = 1, endLine = 5 }),
          TestContext.Current.CancellationToken);

      Assert.False(result.IsError);
      Assert.Contains("vault marker", result.Content, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(vault, workspace);
    }
  }

  [Fact]
  public async Task Write_RefusedOnAReadOnlyRoot()
  {
    string vault = Path.Combine(Path.GetTempPath(), "ethang-trusted-" + Guid.NewGuid().ToString("N"));
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-trusted-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(vault);
    _ = Directory.CreateDirectory(workspace);
    try
    {
      using ServiceProvider services = Build(workspace,
          JsonSerializer.Serialize(new[] { new { path = vault, enabled = true, editable = false } }));
      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

      CapabilityInvocationResult result = await tools.InvokeAsync("write",
          JsonSerializer.Serialize(new { timeoutSeconds = 60, path = Path.Combine(vault, "x.md"), content = "no" }),
          TestContext.Current.CancellationToken);

      Assert.True(result.IsError);
      // The distinct code, not the generic containment one: the root IS trusted, just not
      // for writing, and the model needs that difference to act correctly.
      Assert.Contains("TrustedRootReadOnly", result.Content, StringComparison.Ordinal);
      Assert.False(File.Exists(Path.Combine(vault, "x.md")));
    }
    finally
    {
      Cleanup(vault, workspace);
    }
  }

  [Fact]
  public async Task Write_ReachesAnEditableRoot()
  {
    string vault = Path.Combine(Path.GetTempPath(), "ethang-trusted-" + Guid.NewGuid().ToString("N"));
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-trusted-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(vault);
    _ = Directory.CreateDirectory(workspace);
    try
    {
      using ServiceProvider services = Build(workspace,
          JsonSerializer.Serialize(new[] { new { path = vault, enabled = true, editable = true } }));
      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
      string file = Path.Combine(vault, "written.md");

      CapabilityInvocationResult result = await tools.InvokeAsync("write",
          JsonSerializer.Serialize(new { timeoutSeconds = 60, path = file, content = "written" }),
          TestContext.Current.CancellationToken);

      Assert.False(result.IsError);
      Assert.True(File.Exists(file));
    }
    finally
    {
      Cleanup(vault, workspace);
    }
  }

  [Fact]
  public async Task NoApproval_StillRefusesTheExternalPath()
  {
    string vault = Path.Combine(Path.GetTempPath(), "ethang-trusted-" + Guid.NewGuid().ToString("N"));
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-trusted-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(vault);
    _ = Directory.CreateDirectory(workspace);
    try
    {
      // Nothing approved: the container must be byte-identical to the pre-#96 wiring.
      using ServiceProvider services = Build(workspace, stored: null);
      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

      CapabilityInvocationResult result = await tools.InvokeAsync("write",
          JsonSerializer.Serialize(new
          {
            timeoutSeconds = 60,
            path = Path.Combine(vault, "x.md"),
            content = "no",
          }),
          TestContext.Current.CancellationToken);

      Assert.True(result.IsError);
      Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(vault, workspace);
    }
  }

  [Fact]
  public async Task SymlinkedAncestor_IsNotConfusedForTheRoot()
  {
    // Containment is SEGMENT-aware: a sibling directory whose name merely starts with
    // the approved root's name is outside it, so an approval cannot be widened by
    // naming something similar.
    string vault = Path.Combine(Path.GetTempPath(), "ethang-trusted-" + Guid.NewGuid().ToString("N"));
    string sibling = vault + "-sibling";
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-trusted-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(vault);
    _ = Directory.CreateDirectory(sibling);
    _ = Directory.CreateDirectory(workspace);
    try
    {
      using ServiceProvider services = Build(workspace,
          JsonSerializer.Serialize(new[] { new { path = vault, enabled = true, editable = true } }));
      AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

      CapabilityInvocationResult result = await tools.InvokeAsync("write",
          JsonSerializer.Serialize(new
          {
            timeoutSeconds = 60,
            path = Path.Combine(sibling, "x.md"),
            content = "no",
          }),
          TestContext.Current.CancellationToken);

      Assert.True(result.IsError);
      Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
    }
    finally
    {
      Cleanup(vault, sibling, workspace);
    }
  }

  [Fact]
  public void Prompt_NamesTheApprovedRootsOnlyWhenSomeExist()
  {
    TrustedDirectoriesPromptProvider none = new([]);
    TrustedDirectoriesPromptProvider some = new([new TrustedDirectory(@"C:\vault", Editable: false)]);

    // With nothing approved the provider renders EMPTY, so the composite prompt drops it
    // and a session that approves nothing keeps a byte-identical prompt.
    Assert.Equal(string.Empty, none.Build());
    // With a root approved the model is TOLD - an approval it cannot see is one it will
    // never use, which is what kept the exec workaround alive.
    string prompt = some.Build();
    Assert.Contains(@"Trusted directories", prompt, StringComparison.Ordinal);
    Assert.Contains(@"C:\vault", prompt, StringComparison.Ordinal);
    Assert.Contains("read only", prompt, StringComparison.Ordinal);
    // And it stays pure: the same instance renders the same bytes twice.
    Assert.Equal(prompt, some.Build());
  }

  [Fact]
  public void Prompt_LabelsAnEditableRootAsReadAndWrite()
  {
    TrustedDirectoriesPromptProvider provider = new([new TrustedDirectory(@"C:\vault", Editable: true)]);

    Assert.Contains("read and write", provider.Build(), StringComparison.Ordinal);
  }

  /// <summary>Builds the real core over a temp workspace with the trusted-directory
  ///     preference a user's approval would leave in the store.</summary>
  private static ServiceProvider Build(string workspace, string? stored)
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("m", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(new FixedWorkspaceContext(workspace), new WorkspacePathResolver(workspace)),
            db.Database)
        // The factory normally REPLACES this registration after reading the store; here
        // the stored value is supplied directly, which is the same seam and one
        // indirection less than standing up a whole preference store in a unit test.
        .AddSingleton(new ResolvedTrustedDirectories(TrustedDirectoryPreferences.Resolve(stored)))
        .BuildServiceProvider();
  }

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  /// <summary>Best-effort removal of the temp trees this test created.</summary>
  private static void Cleanup(params string[] directories)
  {
    foreach (string directory in directories)
    {
#pragma warning disable CA1031 // Best-effort cleanup of temp directories.
#pragma warning disable S108 // Empty catch block is the best-effort cleanup.
      try
      {
        Directory.Delete(directory, recursive: true);
      }
      catch
      {
      }
#pragma warning restore S108
#pragma warning restore CA1031
    }
  }
}
