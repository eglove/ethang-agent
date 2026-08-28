using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Two-stage LLM pipeline that categorizes a task and selects the best model+provider
/// pair from the model catalog. Stage 1 categorizes; Stage 2 decides filters and picks. The
/// model that powers both stages is injected via the constructor so the pipeline runs on
/// whichever provider the host wired — the selector itself is provider-agnostic.</summary>
public sealed class IntelligentModelSelector : IModelSelector
{
  private const string SelectionFailed = "SelectionFailed";
  private const string Filter = "filter";

  private readonly IModelProvider _provider;
  private readonly IModelCatalog _catalog;

  // Stage budgets differ (categorize short, decide longer); the model identity comes from the host.
  private readonly ModelConfig _stage1Config;
  private readonly ModelConfig _stage2Config;

  public IntelligentModelSelector(IModelProvider provider, IModelCatalog catalog, ModelConfig selectorModel)
  {
    _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    ArgumentNullException.ThrowIfNull(selectorModel);
    _stage1Config = selectorModel with { MaxTokens = 1024 };
    _stage2Config = selectorModel with { MaxTokens = 2048 };
  }

  private const string Stage1SystemPrompt =
      """
    You are a task categorizer. Given a task prompt, output ONLY valid JSON (no markdown fences) with this schema:
    {"tags": [string], "complexity": number (1-5), "requiresVision": boolean, "requiresToolUse": boolean, "minContextWindow": number|null, "reasoning": string|null}
    Tags must be from: coding, reasoning, creative, simple-lookup, tool-use, analysis, writing, math.
    """;

  private const string Stage2SystemPrompt =
      """
    You are a model+provider selector. Given a task category and a list of candidate model+provider pairs
    with their effective prices (after discount), latency, throughput, capability scores, and features,
    output ONLY valid JSON (no markdown fences) with this schema:
    {"filter": {"maxPromptPricePerToken": number|null, "maxCompletionPricePerToken": number|null,
    "minContextLength": number|null, "maxCompletionTokens": number|null,
    "requireToolUse": boolean|null, "requireVision": boolean|null,
    "minIntelligenceScore": number|null, "minCodingScore": number|null, "minAgenticScore": number|null,
    "maxLatencyMs": number|null, "minThroughputTokensPerSec": number|null},
    "selectedModelId": string, "selectedProviderName": string, "reasoning": string|null}
    The selectedModelId and selectedProviderName MUST be one of the candidate pairs provided.
    """;

  public async Task<Result<ModelSelectionResult>> SelectAsync(
      string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(taskPrompt))
    {
      return Result.Failure<ModelSelectionResult>(new DomainError("InvalidTask", "Task prompt is required."));
    }

    Result<TaskCategory> categoryResult = await CategorizeAsync(taskPrompt, ct).ConfigureAwait(false);
    if (!categoryResult.IsSuccess)
    {
      return Result.Failure<ModelSelectionResult>(categoryResult.Error!);
    }

    Result<IReadOnlyList<ModelProviderEntry>> catalogResult = await _catalog.GetAsync(ct).ConfigureAwait(false);
    if (!catalogResult.IsSuccess)
    {
      return Result.Failure<ModelSelectionResult>(catalogResult.Error!);
    }

    IReadOnlyList<ModelProviderEntry> allModels = catalogResult.Value!;
    if (allModels.Count == 0)
    {
      return Result.Failure<ModelSelectionResult>(new DomainError("CatalogEmpty", "Model catalog is empty."));
    }

    IReadOnlyList<ModelProviderEntry> candidates = PreFilter(allModels, categoryResult.Value!, excludedKeys);
    return candidates.Count == 0
      ? Result.Failure<ModelSelectionResult>(new DomainError("NoMatchingModels",
          "No models match the task's capability requirements."))
      : await SelectFromCandidatesAsync(
        categoryResult.Value!, candidates, ct).ConfigureAwait(false);
  }

  private async Task<Result<TaskCategory>> CategorizeAsync(string taskPrompt, CancellationToken ct)
  {
    Message userMsg = new(Role.User, taskPrompt, DateTimeOffset.UtcNow);
    ModelRequest request = new([userMsg], SystemPrompt: Stage1SystemPrompt);
    Result<ModelResponse> response = await _provider.SendAsync(_stage1Config, request, ct).ConfigureAwait(false);
    return !response.IsSuccess
      ? Result.Failure<TaskCategory>(new DomainError("CategorizationFailed",
          $"Stage 1 LLM call failed: {response.Error!.Message}"))
      : ParseCategory(response.Value!.Content);
  }

  private static Result<TaskCategory> ParseCategory(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return Result.Failure<TaskCategory>(new DomainError("CategorizationFailed", "Stage 1 returned empty content."));
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;

      List<string> tags = [];
      if (root.TryGetProperty("tags", out JsonElement tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
      {
        tags = [.. tagsEl.EnumerateArray().Select(t => t.GetString() ?? "")];
      }

      int complexity = root.TryGetProperty("complexity", out JsonElement c) && c.ValueKind == JsonValueKind.Number
          ? c.GetInt32() : 3;
      bool requiresVision = GetBool(root, "requiresVision");
      bool requiresToolUse = GetBool(root, "requiresToolUse");
      int? minContextWindow = root.TryGetProperty("minContextWindow", out JsonElement mw)
          && mw.ValueKind == JsonValueKind.Number ? mw.GetInt32() : null;
      string? reasoning = root.TryGetProperty("reasoning", out JsonElement r) && r.ValueKind == JsonValueKind.String
          ? r.GetString() : null;

      return Result.Success(new TaskCategory(tags, complexity, requiresVision, requiresToolUse, minContextWindow, reasoning));
    }
    catch (JsonException ex)
    {
      return Result.Failure<TaskCategory>(new DomainError("CategorizationFailed",
          $"Failed to parse Stage 1 JSON: {ex.Message}"));
    }
  }

  private static IReadOnlyList<ModelProviderEntry> PreFilter(
      IReadOnlyList<ModelProviderEntry> models, TaskCategory category, IReadOnlySet<string>? excludedKeys)
  {
    IEnumerable<ModelProviderEntry> filtered = models;

    if (excludedKeys is { Count: > 0 })
    {
      filtered = filtered.Where(m => !excludedKeys.Contains(m.Key));
    }

    if (category.RequiresToolUse)
    {
      filtered = filtered.Where(m => m.SupportsToolUse);
    }

    if (category.RequiresVision)
    {
      filtered = filtered.Where(m => m.SupportsVision);
    }

    if (category.MinContextWindow is int minCtx)
    {
      filtered = filtered.Where(m => m.ContextLength >= minCtx);
    }

    return [.. filtered];
  }

  private async Task<Result<ModelSelectionResult>> SelectFromCandidatesAsync(
      TaskCategory category, IReadOnlyList<ModelProviderEntry> candidates, CancellationToken ct)
  {
    string candidatesJson = SerializeCandidates(candidates);
    string userMessage = $"Task category: {JsonSerializer.Serialize(category)}\n\nCandidate models:\n{candidatesJson}";

    Message msg = new(Role.User, userMessage, DateTimeOffset.UtcNow);
    ModelRequest request = new([msg], SystemPrompt: Stage2SystemPrompt);
    Result<ModelResponse> response = await _provider.SendAsync(_stage2Config, request, ct).ConfigureAwait(false);
    return !response.IsSuccess
      ? Result.Failure<ModelSelectionResult>(new DomainError(SelectionFailed,
          $"Stage 2 LLM call failed: {response.Error!.Message}"))
      : ParseSelection(response.Value!.Content, category, candidates);
  }

  private static string SerializeCandidates(IReadOnlyList<ModelProviderEntry> candidates)
  {
    var slim = candidates.Select(c => new
    {
      id = c.ModelId,
      provider = c.ProviderName,
      promptPrice = c.PromptPricePerToken,
      completionPrice = c.CompletionPricePerToken,
      contextLength = c.ContextLength,
      maxCompletionTokens = c.MaxCompletionTokens,
      supportsToolUse = c.SupportsToolUse,
      supportsVision = c.SupportsVision,
      intelligenceScore = c.IntelligenceScore,
      codingScore = c.CodingScore,
      agenticScore = c.AgenticScore,
      latencyMs = c.LatencyMs,
      throughputTokensPerSec = c.ThroughputTokensPerSec,
      description = c.Description
    });
    return JsonSerializer.Serialize(slim);
  }

  private static Result<ModelSelectionResult> ParseSelection(
      string? json, TaskCategory category, IReadOnlyList<ModelProviderEntry> candidates)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return Result.Failure<ModelSelectionResult>(new DomainError(SelectionFailed, "Stage 2 returned empty content."));
    }

    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;

      ModelFilter filter = new(
          GetNullableDecimal(root, Filter, "maxPromptPricePerToken"),
          GetNullableDecimal(root, Filter, "maxCompletionPricePerToken"),
          GetNullableInt(root, Filter, "minContextLength"),
          GetNullableInt(root, Filter, "maxCompletionTokens"),
          GetNullableBool(root, Filter, "requireToolUse"),
          GetNullableBool(root, Filter, "requireVision"),
          GetNullableDouble(root, Filter, "minIntelligenceScore"),
          GetNullableDouble(root, Filter, "minCodingScore"),
          GetNullableDouble(root, Filter, "minAgenticScore"),
          GetNullableDouble(root, Filter, "maxLatencyMs"),
          GetNullableDouble(root, Filter, "minThroughputTokensPerSec"));

      if (!root.TryGetProperty("selectedModelId", out JsonElement modelEl) || modelEl.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<ModelSelectionResult>(new DomainError(SelectionFailed, "Stage 2 missing selectedModelId."));
      }

      string modelId = modelEl.GetString()!;

      if (!root.TryGetProperty("selectedProviderName", out JsonElement providerEl) || providerEl.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<ModelSelectionResult>(new DomainError(SelectionFailed, "Stage 2 missing selectedProviderName."));
      }

      string providerName = providerEl.GetString()!;

      if (!candidates.Any(c => c.ModelId == modelId && c.ProviderName == providerName))
      {
        return Result.Failure<ModelSelectionResult>(new DomainError("ModelNotFound",
            $"Selected pair '{modelId}:{providerName}' not in candidate list."));
      }

      string? reasoning = root.TryGetProperty("reasoning", out JsonElement r) && r.ValueKind == JsonValueKind.String
          ? r.GetString() : null;

      return Result.Success(new ModelSelectionResult(modelId, providerName, category, filter, reasoning));
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelSelectionResult>(new DomainError(SelectionFailed,
          $"Failed to parse Stage 2 JSON: {ex.Message}"));
    }
  }

  private static bool GetBool(JsonElement parent, string name)
      => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.True;

  private static decimal? GetNullableDecimal(JsonElement root, string parentKey, string name)
  {
    if (root.TryGetProperty(parentKey, out JsonElement parent) && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement el))
    {
      if (el.ValueKind == JsonValueKind.Number)
      {
        return el.GetDecimal();
      }
      if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(),
          System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal d))
      {
        return d;
      }
    }
    return null;
  }

  private static int? GetNullableInt(JsonElement root, string parentKey, string name)
  {
    return root.TryGetProperty(parentKey, out JsonElement parent) && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
      ? el.GetInt32()
      : null;
  }

  private static bool? GetNullableBool(JsonElement root, string parentKey, string name)
  {
    if (!root.TryGetProperty(parentKey, out JsonElement parent) || parent.ValueKind != JsonValueKind.Object
        || !parent.TryGetProperty(name, out JsonElement el))
    {
      return null;
    }

    if (el.ValueKind == JsonValueKind.True)
    {
      return true;
    }

    bool isFalse = el.ValueKind == JsonValueKind.False;
    return isFalse ? false : null;
  }

  private static double? GetNullableDouble(JsonElement root, string parentKey, string name)
  {
    return root.TryGetProperty(parentKey, out JsonElement parent) && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
      ? el.GetDouble()
      : null;
  }
}
