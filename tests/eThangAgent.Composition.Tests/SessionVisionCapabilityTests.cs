using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>THE SESSION'S VISION FACT, asserted through the REAL composed container that serves
///     no turn. The app hands every turn to a conversation host, so its own container never
///     builds a root agent - and a capability answered only from a running agent reported no
///     image input there, withholding every picture a reference or a tool attached.
///     <para>The subject is the container's OWN read_image: the same tool, resolver, and
///     capability the app resolves, so the test fails exactly when the app withholds.</para>
///     <para>THE SECOND TEST IS THE OTHER HALF OF THE CONTRACT: a model the catalog does not
///     call vision-capable still withholds, so the fix cannot pass by claiming vision always.</para></summary>
[Trait("Category", "Process")]
public sealed class SessionVisionCapabilityTests
{
  // A real 3x2 PNG: the tool SNIFFS the bytes, so a signature-only fixture would not do.
  private const string Png3x2Base64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGP4z8DAAMZMEOo/AwMAO+wF/S5FekgAAAAASUVORK5CYII=";

  [Fact]
  public async Task Container_WithNoRunningTurn_AttachesTheImage_ForTheSelectedVisionModel()
  {
    string workspace = TestTempRoot.CreateDirectory("ethang-vision-ws").FullName;
    try
    {
      string picture = Path.Combine(workspace, "picture.png");
      await File.WriteAllBytesAsync(picture, Convert.FromBase64String(Png3x2Base64),
          TestContext.Current.CancellationToken);

      using ServiceProvider services = Build(workspace, visionModel: true);
      await SelectAsync(services, "test/model");

      // No agent was ever built in this container: the capability has only the selection to go on.
      Assert.Null(services.GetService<RootAgentHolder>()?.CurrentConfig);

      ToolResult result = await ReadImageAsync(services, "picture.png");

      Assert.False(result.IsError, result.Content);
      Assert.DoesNotContain("image withheld", result.Content, StringComparison.Ordinal);
      ToolResultImage attached = Assert.Single(result.Images!);
      Assert.Equal("image/png", attached.MediaType);
      Assert.Equal(Png3x2Base64, attached.Base64Data);
    }
    finally
    {
      Directory.Delete(workspace, recursive: true);
    }
  }

  [Fact]
  public async Task Container_WithNoRunningTurn_WithholdsTheImage_ForATextOnlyModel()
  {
    string workspace = TestTempRoot.CreateDirectory("ethang-vision-text-ws").FullName;
    try
    {
      string picture = Path.Combine(workspace, "picture.png");
      await File.WriteAllBytesAsync(picture, Convert.FromBase64String(Png3x2Base64),
          TestContext.Current.CancellationToken);

      using ServiceProvider services = Build(workspace, visionModel: false);
      await SelectAsync(services, "test/text-only");

      ToolResult result = await ReadImageAsync(services, "picture.png");

      Assert.False(result.IsError, result.Content);
      Assert.Contains("image withheld", result.Content, StringComparison.Ordinal);
      Assert.Empty(result.Images ?? []);
    }
    finally
    {
      Directory.Delete(workspace, recursive: true);
    }
  }

  /// <summary>States the session's selection and resolves the fact the way the app does - the
  ///     session view-model refreshes it when a choice is committed.</summary>
  private static async Task SelectAsync(ServiceProvider services, string modelId)
  {
    services.GetRequiredService<SessionModelPreferences>().ModelIds = [modelId];
    await services.GetRequiredService<SessionVisionCache>()
        .RefreshAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>The container's own read_image, invoked exactly as a reference or an exec script
  ///     invokes it.</summary>
  private static async Task<ToolResult> ReadImageAsync(ServiceProvider services, string path)
  {
    CapabilityInvocationResult invoked = await services.GetRequiredService<AgentToolsProvider>()
        .InvokeAsync("read_image", JsonSerializer.Serialize(new { timeoutSeconds = 60, path }),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(invoked.IsError, invoked.Content);
    return new ToolResult(invoked.Content, false, Images: invoked.Images);
  }

  private static ServiceProvider Build(string workspace, bool visionModel)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath(workspace),
                new WorkspacePathResolver(workspace)),
            db.Database)
        // The capability comes from the catalog, so the catalog is what this test states - at
        // the VENDOR-KEYED seam the container reads, because a model's facts are resolved from
        // the catalog of the vendor that serves it.
        .AddKeyedSingleton<IModelCatalog>(Providers.OpenRouter, new Catalog(visionModel))
        .BuildServiceProvider();
  }

  /// <summary>The catalog answer this test states: two models, one vision-capable and one not,
  ///     so the withhold contract is exercised through the same code path.</summary>
  private sealed class Catalog(bool visionModel) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
        [
            Entry("test/model", visionModel),
            Entry("test/text-only", false),
        ]));

    private static ModelProviderEntry Entry(string id, bool vision) => new(
        id, "Provider", 0.000001m, null, 0.000002m, 8192, 4096,
        SupportsToolUse: true, SupportsVision: vision, SupportsTextOutput: true,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null, Description: null);
  }
}
