using System.Globalization;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>Everything the live suite needs from the machine it runs on: the API key, the
///     base URL, the minimum context window a model must have to hold a real harness prompt,
///     and the per-turn budget.
///     <para>ABSENCE IS A SKIP, not a failure (issue #289): a fork's PR has no secret, and an
///     offline machine has no network. Both are ordinary states, so the suite reports the
///     reason and skips rather than turning a missing secret into a red build. xUnit's
///     runtime <see cref="Assert.Skip"/> is what makes this a real skip - a green test that
///     asserted nothing would be the worst of the three options.</para>
///     <para>The KEY VARIABLE is OPENROUTER_API_KEY, the same name CI supplies, so running
///     locally needs no separate configuration: set it and the suite runs.</para></summary>
internal static class LiveModelEnvironment
{
  /// <summary>The credential the suite runs on. Same name CI uses.</summary>
  public const string ApiKeyVariable = "OPENROUTER_API_KEY";

  /// <summary>Optional base-URL override, for a proxy or a recording server.</summary>
  public const string BaseUrlVariable = "ETHANG_LIVE_MODEL_BASE_URL";

  /// <summary>Optional per-turn wall-clock budget, in seconds.</summary>
  public const string BudgetVariable = "ETHANG_LIVE_MODEL_BUDGET_SECONDS";

  /// <summary>Default per-turn budget: generous, because a free model queues, and still a
  ///     bound - a wedged turn must fail rather than hold the job.</summary>
  public const int DefaultBudgetSeconds = 240;

  /// <summary>The smallest context window the suite will accept. A real harness prompt
  ///     (system prompt, skills listing, exec guide) is thousands of tokens before the
  ///     conversation starts, so a 32K model is not a fair test of anything.</summary>
  public const int MinContextLength = 100_000;

  /// <summary>The API key, or null when none is configured.</summary>
  public static string? ApiKey => NullIfBlank(Environment.GetEnvironmentVariable(ApiKeyVariable));

  /// <summary>True when the suite can run at all.</summary>
  public static bool IsConfigured => ApiKey is not null;

  /// <summary>Skips the calling test unless a key is configured, naming the variable to set.
  ///     Every live test calls this FIRST, before it touches the network.</summary>
  public static string RequireApiKey()
  {
    Assert.SkipWhen(ApiKey is null,
        $"live-model test skipped: no {ApiKeyVariable} is configured. Set it to run the suite "
        + "(see README, 'Live-model tests').");
    return ApiKey ?? string.Empty;
  }

  /// <summary>The provider base URL: the override when set, else the real API.</summary>
  public static Uri BaseUrl
  {
    get
    {
      string? configured = NullIfBlank(Environment.GetEnvironmentVariable(BaseUrlVariable));
      return configured is null ? new Uri("https://openrouter.ai/") : new Uri(configured);
    }
  }

  /// <summary>The per-turn budget: a bound, never unbounded, and never so small that a
  ///     queued free model fails on queueing alone.</summary>
  public static TimeSpan Budget
  {
    get
    {
      string? raw = NullIfBlank(Environment.GetEnvironmentVariable(BudgetVariable));
      return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
          && seconds > 0
              ? TimeSpan.FromSeconds(seconds)
              : TimeSpan.FromSeconds(DefaultBudgetSeconds);
    }
  }

  private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Resolves the model the live suite runs on, from the LIVE catalog, through the
///     production <see cref="OpenRouterCatalogClient"/> and the production
///     <see cref="BestFreeModelSelector"/> - the issue's central requirement: the suite must
///     not hardcode a model id, because the best free model changes without a commit here.
///     <para>The resolved id is RETURNED and reported by every test, so a failure names the
///     model that produced it. The catalog is resolved ONCE per process (a static lazy) - the
///     full endpoint crawl takes tens of seconds, and every test in the run wants the same
///     answer.</para>
///     <para>When the selector finds nothing, the suite FAILS rather than skipping: a
///     configured key plus a catalog with no usable free model is a real regression in either
///     the catalog parse or the selection rule, which is exactly what this suite exists to
///     catch. A rate-limited catalog fetch is retried once, then reported.</para></summary>
internal static class LiveModelCatalog
{
  /// <summary>Fetches the live catalog ONCE per process - the full per-model endpoint crawl
  ///     takes tens of seconds and every caller wants the same answer.</summary>
  private static readonly Lazy<Task<Result<IReadOnlyList<ModelProviderEntry>>>> Catalog = new(
      FetchCatalogAsync, LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>Every qualifying model, best first. Empty-with-a-failure is an ANSWER, not a
  ///     crash: a configured key plus a catalog with no usable free model is a real
  ///     regression (a parse defect or a broken selection rule) and the caller reports it.</summary>
  public static async Task<Result<IReadOnlyList<ModelProviderEntry>>> CandidatesAsync()
  {
    Result<IReadOnlyList<ModelProviderEntry>> fetched = await Catalog.Value.ConfigureAwait(false);
    if (!fetched.IsSuccess)
    {
      return fetched;
    }

    IReadOnlyList<ModelProviderEntry> ranked = BestFreeModelSelector.Rank(fetched.Value,
        new BestFreeModelCriteria { MinContextLength = LiveModelEnvironment.MinContextLength });
    return ranked.Count == 0
        ? Result.Failure<IReadOnlyList<ModelProviderEntry>>(new DomainError("NoFreeModelQualified",
            $"the live catalog ({fetched.Value.Count} rows) contained no model that is free "
            + $"(both prices present and zero), produces text, supports tool use, is not a "
            + $"router, and has a context window of at least {LiveModelEnvironment.MinContextLength} "
            + "tokens."))
        : Result.Success(ranked);
  }

  private static async Task<Result<IReadOnlyList<ModelProviderEntry>>> FetchCatalogAsync()
  {
    string apiKey = LiveModelEnvironment.RequireApiKey();
    OpenRouterConfiguration configuration = new(apiKey, LiveModelEnvironment.BaseUrl);
    using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    using OpenRouterCatalogClient client = new(http, configuration);
    return await client.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
  }
}

/// <summary>One model the provider refused at run time, kept as DATA so a run stays
///     auditable: which models were offered, in what order, and what the provider said.</summary>
internal sealed record LiveModelRejection(string ModelId, string Reason)
{
  /// <summary>One line: the model and the provider's own words (first line of them).</summary>
  public string Describe() => $"{ModelId} refused: {Reason}";
}

/// <summary>The model a live run uses, with the facts that make a failure attributable.</summary>
/// <param name="ModelId">The model the provider accepted.</param>
/// <param name="ContextLength">Its catalog context window.</param>
/// <param name="CodingScore">Its coding score, or null when unmeasured.</param>
/// <param name="IntelligenceScore">Its intelligence score, or null when unmeasured.</param>
/// <param name="AgenticScore">Its agentic score, or null when unmeasured.</param>
/// <param name="RejectedBeforeIt">Models a HIGHER rank offered that the provider refused at run
///     time, with the provider's own words. Empty on a first-attempt hit.
///     <para>THIS FIELD IS THE POINT OF THE WALK: measured 2026-10-26, the catalog's
///     best-scoring free model answered every request with HTTP 403 ("only available on
///     agentic harnesses"). The catalog was not wrong - the refusal is not catalog data.
///     Recording it keeps the run attributable AND visible: a reader of a failing log sees
///     which models were tried, why they were dropped, and what actually served.</para></param>
internal sealed record LiveModelChoice(string ModelId, int ContextLength, double? CodingScore,
    double? IntelligenceScore, double? AgenticScore, IReadOnlyList<LiveModelRejection> RejectedBeforeIt)
{
  /// <summary>One line naming the model and why it was picked: printed by every test's
  ///     failure message, so a red run says which model answered.</summary>
  /// <summary>The models skipped before this one, one line each, or empty.</summary>
  public string DescribeRejections() => RejectedBeforeIt.Count == 0
      ? "none"
      : string.Join("; ", RejectedBeforeIt.Select(r => r.Describe()));

  /// <summary>The ranked-and-refused models, reported as data rather than log noise.</summary>
  public string Describe() => string.Create(CultureInfo.InvariantCulture,
      $"{ModelId} (ctx {ContextLength}, coding {CodingScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unmeasured"}, "
      + $"intelligence {IntelligenceScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unmeasured"}, "
      + $"agentic {AgenticScore?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unmeasured"})");
}
