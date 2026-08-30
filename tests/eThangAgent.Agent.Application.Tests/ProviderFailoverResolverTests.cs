using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class ProviderFailoverResolverTests
{
  private const string FallbackModel = "openrouter/auto";
  private sealed class FakeModelSelector : IModelSelector
  {
    private readonly Queue<ModelSelectionResult> _results = new();
    public List<IReadOnlySet<string>?> ReceivedExcludedKeys { get; } = [];

    public FakeModelSelector(params ModelSelectionResult[] results)
    {
      foreach (ModelSelectionResult r in results)
      {
        _results.Enqueue(r);
      }
    }

    public Task<Result<ModelSelectionResult>> SelectAsync(
        string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
    {
      ReceivedExcludedKeys.Add(excludedKeys);
      return _results.Count > 0
          ? Task.FromResult(Result.Success(_results.Dequeue()))
          : Task.FromResult(Result.Failure<ModelSelectionResult>(
              new DomainError("NoMatchingModels", "all excluded")));
    }
  }

  private sealed class FakeExclusionStore : IProviderExclusionStore
  {
    public HashSet<string> Exclusions { get; } = [];
    public List<(string Key, TimeSpan Ttl)> Added { get; } = [];

    public Task<IReadOnlySet<string>> GetActiveExclusionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Exclusions));

    public Task<bool> AddExclusionAsync(string modelProviderKey, TimeSpan ttl, CancellationToken ct = default)
    {
      _ = Exclusions.Add(modelProviderKey);
      Added.Add((modelProviderKey, ttl));
      return Task.FromResult(true);
    }

    public Task<bool> RemoveExclusionAsync(string modelProviderKey, CancellationToken ct = default)
    {
      _ = Exclusions.Remove(modelProviderKey);
      return Task.FromResult(true);
    }
  }

  private static ModelSelectionResult Selection(string model, string provider) => new(model, provider,
      new TaskCategory(["coding"], 3, false, false, null, null),
      new ModelFilter(null, null, null, null, null, null, null, null, null, null, null), "reason");

  [Fact]
  public async Task ReSelectExcluding_RecordsExclusion_AndReSelects()
  {
    FakeModelSelector selector = new(Selection("new-model", "NewProvider"));
    FakeExclusionStore exclusions = new();
    ProviderFailoverResolver resolver = new(selector, exclusions,
        identity: null, store: null, fallbackModelId: FallbackModel, maxTokens: 2048, temperature: 0.7f, windowSource: new FixedWindowSource());

    (ModelConfig? config, string? notice) = await resolver.ReSelectExcludingAsync(
        "failed-model", "FailedProvider", "task prompt", ct: TestContext.Current.CancellationToken);

    Assert.Equal("new-model", config.ModelId);
    Assert.Equal("NewProvider", config.Provider);
    Assert.Contains("failed-model:FailedProvider", exclusions.Exclusions);
    Assert.NotNull(notice);
    Assert.Contains("failed-model", notice, StringComparison.Ordinal);
    Assert.Contains("FailedProvider", notice, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReSelectExcluding_AllExcluded_FallsBackToAuto()
  {
    FakeModelSelector selector = new(); // returns failure (empty queue)
    FakeExclusionStore exclusions = new();
    ProviderFailoverResolver resolver = new(selector, exclusions,
        identity: null, store: null, fallbackModelId: FallbackModel, maxTokens: 2048, temperature: 0.7f, windowSource: new FixedWindowSource());

    (ModelConfig? config, string? notice) = await resolver.ReSelectExcludingAsync(
        "failed-model", "FailedProvider", "task prompt", ct: TestContext.Current.CancellationToken);

    Assert.Equal(FallbackModel, config.ModelId);
    Assert.Null(config.Provider);
    Assert.NotNull(notice);
  }

  [Fact]
  public async Task ReSelectExcluding_PassesExistingExclusionsPlusNewOne()
  {
    FakeModelSelector selector = new(Selection("new-model", "NewProvider"));
    FakeExclusionStore exclusions = new();
    _ = exclusions.Exclusions.Add("prior:model");
    ProviderFailoverResolver resolver = new(selector, exclusions,
        identity: null, store: null, fallbackModelId: FallbackModel, maxTokens: 2048, temperature: 0.7f, windowSource: new FixedWindowSource());

    _ = await resolver.ReSelectExcludingAsync("failed", "Failed", "task", ct: TestContext.Current.CancellationToken);

    IReadOnlySet<string>? received = Assert.Single(selector.ReceivedExcludedKeys);
    Assert.Contains("prior:model", received!);
    Assert.Contains("failed:Failed", received!);
  }
}
