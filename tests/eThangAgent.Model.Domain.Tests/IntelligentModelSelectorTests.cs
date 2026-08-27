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

  private sealed class FakeModelCatalog(IReadOnlyList<ModelCatalogEntry> entries) : IModelCatalog
  {
    private readonly IReadOnlyList<ModelCatalogEntry> _entries = entries;
    public Task<Result<IReadOnlyList<ModelCatalogEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(_entries));
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelCatalogEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<ModelCatalogEntry>>(
            new DomainError("CatalogUnavailable", "down")));
  }

  private static readonly IReadOnlyList<ModelCatalogEntry> SampleCatalog =
  [
    new("google/gemini-2.0-flash-001", 0.000001m, 0.000002m, 1_048_576, true, true, 85.0, "fast"),
    new("anthropic/claude-3.5-sonnet", 0.000003m, 0.000005m, 200_000, true, false, 90.0, "smart"),
    new("meta-llama/llama-3.3-70b", 0.0000005m, 0.0000008m, 131_072, false, false, 75.0, null),
  ];

  private const string Stage1Json =
      """{"tags":["coding","tool-use"],"complexity":4,"requiresVision":false,"requiresToolUse":true,"minContextWindow":null,"reasoning":"coding task"}""";

  private const string Stage2Json =
      """{"filter":{"maxPromptPricePerToken":0.000005,"requireToolUse":true,"minQualityScore":80.0},"selectedModelId":"anthropic/claude-3.5-sonnet","reasoning":"best for coding"}""";

  [Fact]
  public async Task SelectAsync_HappyPath_ReturnsSelectedModel()
  {
    FakeModelProvider provider = new(Stage1Json, Stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    Result<ModelSelectionResult> result = await selector.SelectAsync("write a C# function");

    Assert.True(result.IsSuccess);
    Assert.Equal("anthropic/claude-3.5-sonnet", result.Value!.ModelId);
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
    string hallucinatedJson = """{"filter":{},"selectedModelId":"nonexistent/model","reasoning":"oops"}""";
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
    string stage1Json = """{"tags":["coding"],"complexity":3,"requiresVision":false,"requiresToolUse":false,"minContextWindow":200000,"reasoning":"needs long context"}""";
    string stage2Json = """{"filter":{},"selectedModelId":"google/gemini-2.0-flash-001","reasoning":"ok"}""";
    FakeModelProvider provider = new(stage1Json, stage2Json);
    FakeModelCatalog catalog = new(SampleCatalog);
    IntelligentModelSelector selector = new(provider, catalog);

    _ = await selector.SelectAsync("task");

    string stage2Message = provider.ReceivedUserMessages[1];
    Assert.Contains("google/gemini-2.0-flash-001", stage2Message, StringComparison.Ordinal);
    Assert.Contains("anthropic/claude-3.5-sonnet", stage2Message, StringComparison.Ordinal);
    Assert.DoesNotContain("meta-llama/llama-3.3-70b", stage2Message, StringComparison.Ordinal);
  }
}