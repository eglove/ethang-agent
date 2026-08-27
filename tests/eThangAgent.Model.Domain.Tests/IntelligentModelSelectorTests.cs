using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Model.Domain.Tests;

public class IntelligentModelSelectorTests
{
  private sealed class FakeModelProvider : IModelProvider
  {
    private readonly Queue<string> _responses = new();
    public List<string> ReceivedUserMessages { get; } = [];
    public List<string> ReceivedSystemPrompts { get; } = [];

    public FakeModelProvider(params string[] responses)
    {
      foreach (string r in responses)
      {
        _responses.Enqueue(r);
      }
    }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      ReceivedUserMessages.Add(request.Messages.Count > 0 ? request.Messages[0].Content : "");
      ReceivedSystemPrompts.Add(request.SystemPrompt ?? "");
      string content = _responses.Count > 0 ? _responses.Dequeue() : "{}";
      return Task.FromResult(Result.Success(new ModelResponse(content, [])));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private sealed class FakeModelCatalog(IReadOnlyList<ModelProviderEntry> entries) : IModelCatalog
  {
    private readonly IReadOnlyList<ModelProviderEntry> _entries = entries;
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(_entries));
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogUnavailable", "down")));
  }

  private static readonly IReadOnlyList<ModelProviderEntry> SampleCatalog =
  [
    new("google/gemini-2.0-flash-001", "Google", 0.000001m, 0.000002m, 1_048_576, 8192, true, true, 85.0, 80.0, 70.0, null, null, "fast"),
    new("anthropic/claude-3.5-sonnet", "Anthropic", 0.000003m, 0.000005m, 200_000, 8192, true, false, 90.0, 95.0, 88.0, null, null, "smart"),
    new("meta-llama/llama-3.3-70b", "Meta", 0.0000005m, 0.0000008m, 131_072, 4096, false, false, 75.0, 70.0, 60.0, null, null, null),
  ];

  private const string Stage1Json =
                           /*lang=json,strict*/
                           """{"tags":["coding","tool-use"],"complexity":4,"requiresVision":false,"requiresToolUse":true,"minContextWindow":null,"reasoning":"coding task"}""";

  private const string Stage2Json =
                           /*lang=json,strict*/
                           """{"filter":{"maxPromptPricePerToken":0.000005,"requireToolUse":true,"minIntelligenceScore":80.0},"selectedModelId":"anthropic/claude-3.5-sonnet","selectedProviderName":"Anthropic","reasoning":"best for coding"}""";

  [Fact]
  public async Task SelectAsync_HappyPath_ReturnsSelectedModelAndProvider()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("write a C# function");

    Assert.True(result.IsSuccess);
    Assert.Equal("anthropic/claude-3.5-sonnet", result.Value!.ModelId);
    Assert.Equal("Anthropic", result.Value.ProviderName);
    Assert.True(result.Value.Category.RequiresToolUse);
    Assert.Equal(4, result.Value.Category.Complexity);
    Assert.True(result.Value.AppliedFilter.RequireToolUse);
    Assert.Equal("best for coding", result.Value.Reasoning);
  }

  [Fact]
  public async Task SelectAsync_Stage1ParseFailure_ReturnsFailure()
  {
    FakeModelProvider provider = new("not json at all", "{}");
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("task");

    Assert.False(result.IsSuccess);
    Assert.Equal("CategorizationFailed", result.Error!.Code);
  }

  [Fact]
  public async Task SelectAsync_Stage2ParseFailure_ReturnsFailure()
  {
    FakeModelProvider provider = new(Stage1Json, "not json");
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("task");

    Assert.False(result.IsSuccess);
    Assert.Equal("SelectionFailed", result.Error!.Code);
  }

  [Fact]
  public async Task SelectAsync_Stage2HallucinatedModelId_ReturnsFailure()
  {
    string hallucinatedJson = /*lang=json,strict*/ """{"filter":{},"selectedModelId":"nonexistent/model","selectedProviderName":"Fake","reasoning":"oops"}""";
    FakeModelProvider provider = new(Stage1Json, hallucinatedJson);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("task");

    Assert.False(result.IsSuccess);
    Assert.Equal("ModelNotFound", result.Error!.Code);
  }

  [Fact]
  public async Task SelectAsync_CatalogEmpty_ReturnsFailure()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FakeModelCatalog catalog = new([]);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("task");

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogEmpty", result.Error!.Code);
  }

  [Fact]
  public async Task SelectAsync_CatalogFetchFailure_ReturnsFailure()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FailingCatalog catalog = new();
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("task");

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogUnavailable", result.Error!.Code);
  }

  [Fact]
  public async Task SelectAsync_PreFilter_DropsNonToolUseModelsWhenRequired()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    _ = await selector.SelectAsync("task");

    Assert.Equal(2, provider.ReceivedUserMessages.Count);
    string stage2Message = provider.ReceivedUserMessages[1];
    Assert.Contains("anthropic/claude-3.5-sonnet", stage2Message, StringComparison.Ordinal);
    Assert.Contains("google/gemini-2.0-flash-001", stage2Message, StringComparison.Ordinal);
    Assert.DoesNotContain("meta-llama/llama-3.3-70b", stage2Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelectAsync_PreFilter_DropsBelowMinContextWindow()
  {
    string stage1Json = /*lang=json,strict*/ """{"tags":["coding"],"complexity":3,"requiresVision":false,"requiresToolUse":false,"minContextWindow":200000,"reasoning":"needs long context"}""";
    string stage2Json = /*lang=json,strict*/ """{"filter":{},"selectedModelId":"google/gemini-2.0-flash-001","selectedProviderName":"Google","reasoning":"ok"}""";
    FakeModelProvider provider = new(stage1Json, stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    _ = await selector.SelectAsync("task");

    string stage2Message = provider.ReceivedUserMessages[1];
    Assert.Contains("google/gemini-2.0-flash-001", stage2Message, StringComparison.Ordinal);
    Assert.Contains("anthropic/claude-3.5-sonnet", stage2Message, StringComparison.Ordinal);
    Assert.DoesNotContain("meta-llama/llama-3.3-70b", stage2Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelectAsync_WithExclusion_DropsExcludedProviderKeepsSameModelViaOtherProvider()
  {
    List<ModelProviderEntry> catalogWithSecondProvider = [
      ..SampleCatalog,
      new("anthropic/claude-3.5-sonnet", "OpenRouter", 0.000004m, 0.000006m, 200_000, 8192, true, false, 90.0, 95.0, 88.0, null, null, "smart"),
    ];
    string stage2Json = /*lang=json,strict*/ """{"filter":{},"selectedModelId":"anthropic/claude-3.5-sonnet","selectedProviderName":"OpenRouter","reasoning":"fallback provider"}""";
    FakeModelProvider provider = new(Stage1Json, stage2Json);
    FakeModelCatalog catalog = new(catalogWithSecondProvider);
    IntelligentModelSelector selector = new(provider, catalog);

    HashSet<string> excluded = ["anthropic/claude-3.5-sonnet:Anthropic"];
    Result<ModelSelectionResult> result = await selector.SelectAsync("task", excluded);

    Assert.True(result.IsSuccess);
    Assert.Equal("anthropic/claude-3.5-sonnet", result.Value!.ModelId);
    Assert.Equal("OpenRouter", result.Value.ProviderName);
  }

  [Fact]
  public async Task SelectAsync_AllCandidatesExcluded_ReturnsNoMatchingModels()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    HashSet<string> excluded = [.. SampleCatalog.Select(c => c.Key)];
    Result<ModelSelectionResult> result = await selector.SelectAsync("task", excluded);

    Assert.False(result.IsSuccess);
    Assert.Equal("NoMatchingModels", result.Error!.Code);
  }
}
