using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Zai.ACL;

/// <summary>Static catalog of z.ai's GLM chat models. z.ai exposes no models-listing
///     endpoint (confirmed against the published OpenAPI spec), so entries are curated
///     from the model and pricing doc pages. This catalog IS the session's selectable
///     lineup: z.ai sessions run no automatic selection — the user picks one of these
///     models through the /model command, and glm-5.3-flash is the default. Prices are
///     LIST (non-promotional) USD per token; context windows are the advertised figures;
///     capability scores are omitted (z.ai publishes none) — descriptions carry the
///     signal instead. Extend by adding entries after verifying limits against the
///     per-model doc pages.</summary>
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
  ];

  public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success(Entries));
}
