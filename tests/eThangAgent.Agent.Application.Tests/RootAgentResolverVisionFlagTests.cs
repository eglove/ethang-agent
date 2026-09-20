using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The resolved root config exposes the catalog's vision capability:
///     resolvers stamp AcceptsImageInput from the session catalog the way they
///     resolve the context window, so the flag survives model resolution and the
///     ModelPreferencesOverlay copy on every resolved config.</summary>
public class RootAgentResolverVisionFlagTests
{
  private const string FallbackModel = "openrouter/auto";

  /// <summary>Catalog with one vision-capable entry and one text-only entry.</summary>
  private sealed class VisionCatalog(params (string ModelId, bool Vision)[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
            [.. entries.Select(e => new ModelProviderEntry(
                e.ModelId, "TestProvider", 0.000001m, 0.000002m, 128_000, 8_192,
                SupportsToolUse: true, SupportsVision: e.Vision,
                null, null, null, null, null, null))]));
  }

  private static RootModelContext Ctx(IModelCatalog catalog) =>
      new(null, null, FallbackModel, 2048, 0.7f, new FixedWindowSource(), catalog);

  private static Task<RootAgentResolver> ResolverAsync(IModelCatalog catalog)
  {
    Task<RootAgentResolver> completed = Task.FromResult(new RootAgentResolver(Ctx(catalog), selector: null));
    return completed;
  }

  [Fact]
  public async Task VisionCapableModel_ResolvedConfig_ExposesFlagTrue()
  {
    RootAgentResolver resolver = await ResolverAsync(new VisionCatalog((FallbackModel, true)));

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task", ct: TestContext.Current.CancellationToken);

    Assert.True(config.AcceptsImageInput);
  }

  [Fact]
  public async Task TextOnlyModel_ResolvedConfig_ExposesFlagFalse()
  {
    RootAgentResolver resolver = await ResolverAsync(new VisionCatalog((FallbackModel, false)));

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task", ct: TestContext.Current.CancellationToken);

    Assert.False(config.AcceptsImageInput);
  }

  [Fact]
  public async Task NoCatalog_Wired_ResolvedConfig_DefaultsFalse()
  {
    RootAgentResolver withoutCatalog = new(
        new RootModelContext(null, null, FallbackModel, 2048, 0.7f, new FixedWindowSource(), Catalog: null),
        selector: null);

    (ModelConfig config, _) = await withoutCatalog.ResolveAsync(new Conversation(), "task", ct: TestContext.Current.CancellationToken);

    Assert.False(config.AcceptsImageInput);
  }
}
