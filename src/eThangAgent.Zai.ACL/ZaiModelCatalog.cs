using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

/// <summary>Static catalog of z.ai's GLM chat models. z.ai exposes no models-listing
///     endpoint (confirmed against the published OpenAPI spec), so entries are curated
///     from the model and pricing doc pages. Prices are LIST (non-promotional) USD per
///     token; context windows are the advertised figures; capability scores are omitted
///     (z.ai publishes none) — descriptions carry the signal instead. Extend by adding
///     entries after verifying limits against the per-model doc pages.</summary>
public sealed class ZaiModelCatalog : IModelCatalog
{
  /// <summary>Serving-provider name stamped on every entry; also the exclusion-key
  ///     discriminator, chosen so it cannot collide with OpenRouter upstream names.</summary>
  public const string ProviderName = "z.ai";

  private static readonly IReadOnlyList<ModelProviderEntry> Entries =
  [
    new("glm-5.3", ProviderName, 0.0000014m, 0.0000044m, 1_000_000, 131_072,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Current GLM flagship: forced deep thinking, 1M context, strongest coding and agentic option."),
    new("glm-5.3-flash", ProviderName, 0.00000015m, 0.0000005m, 1_000_000, 131_072,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Fast, cheap derivative of the flagship with the same 1M context; good default for routine coding and tool use."),
    new("glm-4.7", ProviderName, 0.0000006m, 0.0000022m, 200_000, 131_072,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Previous-generation workhorse: strong tool use and coding benchmarks at mid price."),
    new("glm-4.7-flash", ProviderName, 0m, 0m, 200_000, 131_072,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Free tier of the 4.7 generation; expect tighter rate limits than paid models."),
    new("glm-4.6", ProviderName, 0.0000006m, 0.0000022m, 200_000, 131_072,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Older flagship tuned for tool use and search-driven agents; 200K context."),
    new("glm-4.5-air", ProviderName, 0.0000002m, 0.0000011m, 128_000, 98_304,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Lightweight hybrid-reasoning model optimized for tool invocation at a low price."),
    new("glm-4.5-flash", ProviderName, 0m, 0m, 128_000, 98_304,
        SupportsToolUse: true, SupportsVision: false,
        IntelligenceScore: null, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null,
        "Free lightweight model; fine for classification-scale work, rate-limited."),
  ];

  public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success(Entries));
}
