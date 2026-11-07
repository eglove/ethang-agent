using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Lsp;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The rename action's write half is wired at composition (issue #357): the container
///     registers the applier over the SAME file seams and workspace root every other mutating
///     tool uses, and the 'lsp' action advertises the rename surface the model reads.</summary>
[Trait("Category", "Process")]
public class LspRenameCompositionTests
{
  private static ServiceProvider Build(string root)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    AppDatabase database = new(Path.Combine(TestTempRoot.Root, $"ethang-lspcomp-{Guid.NewGuid():N}.db"));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath(root), new WorkspacePathResolver(root)), database)
        .BuildServiceProvider();
  }

  private static string NewRoot()
  {
    string root = Path.Combine(TestTempRoot.Root, $"ethang-lspws-{Guid.NewGuid():N}");
    _ = Directory.CreateDirectory(root);
    return root;
  }

  [Fact]
  public void Lsp_Action_Advertises_Rename_And_Apply()
  {
    using ServiceProvider services = Build(NewRoot());
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    ResolvedCapability lsp = surface().Resolve("lsp").Value
        ?? throw new InvalidOperationException("the capability surface does not carry 'lsp'");
    Assert.Contains("rename", lsp.Action.Description, StringComparison.Ordinal);
    Assert.Contains("apply", lsp.Action.Description, StringComparison.Ordinal);
    Assert.Contains(lsp.Action.Parameters, p => p.Name == "newName");
    Assert.Contains(lsp.Action.Parameters, p => p.Name == "apply");
  }

  [Fact]
  public async Task Container_Applier_Writes_A_Rename_Through_The_Workspace_Seams()
  {
    string root = NewRoot();
    string file = Path.Combine(root, "A.cs");
    await File.WriteAllTextAsync(file, "var foo = 1;" + Environment.NewLine,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    using ServiceProvider services = Build(root);
    ILspRenameApplier applier = services.GetRequiredService<ILspRenameApplier>();

    Result<LspRenameApplied> applied = await applier
        .ApplyAsync([new LspRenameEdit(file, 0, 4, 0, 7, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(applied.IsSuccess, applied.Error?.Message);
    Assert.Equal(1, applied.Value.EditCount);
    Assert.Contains("var bar = 1;", await File.ReadAllTextAsync(file,
        TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Container_Applier_Refuses_A_Path_Outside_The_Workspace()
  {
    string root = NewRoot();
    string outside = Path.Combine(TestTempRoot.Root, $"ethang-lspout-{Guid.NewGuid():N}.cs");
    await File.WriteAllTextAsync(outside, "var foo = 1;" + Environment.NewLine,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    using ServiceProvider services = Build(root);
    ILspRenameApplier applier = services.GetRequiredService<ILspRenameApplier>();

    Result<LspRenameApplied> applied = await applier
        .ApplyAsync([new LspRenameEdit(outside, 0, 4, 0, 7, "bar")], TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(applied.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", applied.Error?.Code);
    Assert.Contains("var foo = 1;", await File.ReadAllTextAsync(outside,
        TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
  }
}
