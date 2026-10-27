using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Issue #251 end-to-end through the real container: the workspace's own
///     .editorconfig decides the convention a NEW file lands in, the composed write seam
///     applies it, and the advertised `write` tool description tells the model so.
///     Drives the composed seams — no fake on the path under test.</summary>
public sealed class LineEndingCompositionTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-eolc").FullName;

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(new FixedWorkspaceContext(_root), new WorkspacePathResolver(_root)),
            db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void ResolvedPolicy_ReadsTheWorkspacesEditorConfig()
  {
    File.WriteAllText(Path.Combine(_root, ".editorconfig"), "root = true\n\n[*]\nend_of_line = crlf\n");
    using ServiceProvider services = Build();

    ILineEndingPolicy policy = services.GetRequiredService<ILineEndingPolicy>();

    Assert.Equal(LineEnding.CrLf, policy.Resolve(Path.Combine(_root, "brand-new.txt")));
  }

  [Fact]
  public async Task ComposedWriteSeam_LandsTheProjectsDeclaredConvention()
  {
    await File.WriteAllTextAsync(Path.Combine(_root, ".editorconfig"), "root = true\n\n[*]\nend_of_line = crlf\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
    using ServiceProvider services = Build();
    IFileWriteAccess files = services.GetRequiredService<IFileWriteAccess>();
    string target = Path.Combine(_root, "fresh.txt");

    Result<FileWriteOutcome> written = await files.WriteFileAsync(
        target, "a\nb", overwrite: false, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(written.IsSuccess);
    Assert.Equal(LineEnding.CrLf, written.Value.NormalizedTo);
    Assert.Equal("a\r\nb", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public void AdvertisedWriteTool_TellsTheModelEndingsAreNormalized()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();

    string description = Assert.Single(tools.Actions, a => a.Name == "write").Description;

    Assert.Contains("LINE ENDINGS ARE NORMALIZED MECHANICALLY", description, StringComparison.Ordinal);
    Assert.Contains("line endings", description, StringComparison.Ordinal);
  }
}
