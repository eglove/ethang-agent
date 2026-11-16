using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
namespace eThangAgent.OpenRouter.ACL.Tests;

// The Cheapest variant on the wire: provider.order carrying the model's priced endpoints ordered
// by total cost per input token at the workspace's measured usage shape, with fallbacks allowed so
// an unavailable endpoint cannot fail the turn. Every other variant sends its own order or none.
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
public class CheapestWireTests
{
  private const string BaseModel = "alpha/model";
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  /// <summary>The workspace median shape the ledger reports: 90.13% cache hits, 0.007284 output
  ///     tokens per input token.</summary>
  private static readonly CostShape WorkspaceShape = new(0.9013m, 0.007284m);

  private static ModelProviderEntry Endpoint(string? slug, decimal? input, decimal? cacheRead, decimal? output)
      => new(BaseModel, "Provider", input, cacheRead, output, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
      {
        Variant = slug is null ? EndpointVariant.Unspecified : new EndpointVariant(slug),
      };

  /// <summary>The two priced endpoints of the brief's first example: a/fp4 is cheaper at every
  ///     rate, so it leads at any shape.</summary>
  private static ModelProviderEntry[] TwoPricedEndpoints()
      =>
      [
          Endpoint("a/fp4", 1e-7m, 1e-8m, 2e-7m),
          Endpoint("b/fp8", 2e-7m, 3e-8m, 4e-7m),
      ];

  [Fact]
  public async Task Body_Cheapest_SendsTheOrderCheapestTotalCostFirst()
  {
    string body = await CaptureBodyAsync(ModelVariant.Cheapest, TwoPricedEndpoints(),
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.Equal(["a/fp4", "b/fp8"],
        provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
    Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Body_Cheapest_LeavesTheModelIdBare_BecauseTheProviderDocumentsNoSuffix()
  {
    string body = await CaptureBodyAsync(ModelVariant.Cheapest, TwoPricedEndpoints(),
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(BaseModel, doc.RootElement.GetProperty("model").GetString());
  }

  [Fact]
  public async Task Body_Cheapest_OrdersByTotalCost_NotByCacheReadPriceAlone()
  {
    // The two orders disagree here: b/fp16 advertises the cheaper cache READ, but its input price
    // is ten times a/fp4's, so total cost puts a/fp4 first. This is the whole reason the variant
    // exists, and it is what tells a Cheapest request apart from a Cache Floor one.
    ModelProviderEntry[] rows =
    [
        Endpoint("a/fp4", 1e-8m, 5e-8m, 1e-7m),
        Endpoint("b/fp16", 1e-6m, 1e-8m, 1e-7m),
    ];

    string cheapest = await CaptureBodyAsync(ModelVariant.Cheapest, rows,
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);
    string cacheFloor = await CaptureBodyAsync(ModelVariant.CacheFloor, rows,
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);

    using JsonDocument cheapestDoc = JsonDocument.Parse(cheapest);
    Assert.Equal(["a/fp4", "b/fp16"], cheapestDoc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));

    using JsonDocument floorDoc = JsonDocument.Parse(cacheFloor);
    Assert.Equal(["b/fp16", "a/fp4"], floorDoc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_Cheapest_WithNoEndpointSlug_SendsNoProviderObject()
  {
    // An order names endpoints. A catalog that tags none cannot name one, so the variant degrades
    // to the provider's own routing rather than sending an instruction that names nothing.
    string body = await CaptureBodyAsync(ModelVariant.Cheapest,
        [Endpoint(null, 1e-7m, 1e-8m, 2e-7m)], new FakeUsageQueries(WorkspaceShape))
        .ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task Body_Cheapest_OmitsARowWhoseInputPriceIsUnknown()
  {
    // Unknown is never cheapest: a row with no advertised input price cannot be priced, so it is
    // not named in an order that claims to rank endpoints by cost.
    ModelProviderEntry[] rows =
    [
        Endpoint("a/fp4", 1e-7m, 1e-8m, 2e-7m),
        Endpoint("c/fp16", null, 1e-9m, 2e-7m),
    ];

    string body = await CaptureBodyAsync(ModelVariant.Cheapest, rows,
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(["a/fp4"], doc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_Cheapest_WithNoPricedEndpoint_SendsNoProviderObject()
  {
    string body = await CaptureBodyAsync(ModelVariant.Cheapest,
        [Endpoint("c/fp16", null, 1e-9m, 2e-7m)], new FakeUsageQueries(WorkspaceShape))
        .ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task Body_Cheapest_WhenTheCatalogReadFails_StillSendsAValidRequest()
  {
    // A routing preference must never make a model unservable: a catalog that cannot be read
    // leaves the request intact and the provider's own routing in charge.
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config, catalog: new FailingCatalog(),
        usageQueries: new FakeUsageQueries(WorkspaceShape))
    { WorkspaceId = "ws-1" };

    Result<ModelResponse> result = await provider.SendAsync(Model(ModelVariant.Cheapest), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.Equal(BaseModel, doc.RootElement.GetProperty("model").GetString());
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task Body_Cheapest_WithAnUnwiredUsageQuery_StillSendsAnOrderAtTheDefaultShape()
  {
    // The catalog alone is enough to price the endpoints, so an unwired ledger resolves the
    // documented default rung and the order is still sent.
    string body = await CaptureBodyAsync(ModelVariant.Cheapest, TwoPricedEndpoints(),
        usage: null).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(["a/fp4", "b/fp8"], doc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_Cheapest_WithAFailingUsageQuery_StillSendsTheOrderFromTheDefaultShape()
  {
    string body = await CaptureBodyAsync(ModelVariant.Cheapest, TwoPricedEndpoints(),
        new FakeUsageQueries(WorkspaceShape) { FailAll = true }).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.True(doc.RootElement.GetProperty("provider").TryGetProperty("order", out _));
  }

  [Fact]
  public async Task Body_Cheapest_WithAnEnforcedTier_CarriesBothTheOrderAndTheOnlyList()
  {
    // The two keys coexist: only narrows the pool to what the tier admits, order prefers within
    // it. Neither overwrites the other.
    string body = await CaptureBodyAsync(ModelVariant.Cheapest, TwoPricedEndpoints(),
        new FakeUsageQueries(WorkspaceShape), retention: RetentionPolicy.ZeroDataRetention,
        eligible: ["a/fp4", "b/fp8"]).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    Assert.True(provider.GetProperty("zdr").GetBoolean());
    Assert.Equal(["a/fp4", "b/fp8"],
        provider.GetProperty("only").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(["a/fp4", "b/fp8"],
        provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Theory]
  [InlineData(ModelVariant.Standard)]
  [InlineData(ModelVariant.Nitro)]
  [InlineData(ModelVariant.Floor)]
  public async Task Body_EveryVariantWithoutAnOrder_SendsNoProviderObject(ModelVariant variant)
  {
    string body = await CaptureBodyAsync(variant, TwoPricedEndpoints(),
        new FakeUsageQueries(WorkspaceShape)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task Body_Cheapest_OrdersFromTheModelRung_WhenTheLedgerMeasuredThisModel()
  {
    // The endpoint rung cannot answer for a model-level variant, so the model rung carries it -
    // and it outranks the workspace median. These two endpoints swap places between the two
    // shapes, so the order on the wire states which rung actually answered.
    ModelProviderEntry[] rows =
    [
        Endpoint("a/fp4", 1e-7m, 1e-9m, 1e-7m),
        Endpoint("b/fp8", 2e-8m, 2e-8m, 1e-7m),
    ];

    // 2,000,000 input tokens with nothing cached: a genuine 0% hit rate, which is evidence.
    ProviderPerformance measured = new(BaseModel, "Provider", null, null, null, null, 0, 0, 0, 0, 40,
        2_000_000, 0)
    {
      OutputTokens = 14_568,
    };
    ProviderPerformanceReport report = new(UsageScope.Workspace, 0m, 2_000_000, 0, 40, 0, [measured]);

    string body = await CaptureBodyAsync(ModelVariant.Cheapest, rows,
        new FakeUsageQueries(WorkspaceShape) { Report = report }).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal(["b/fp8", "a/fp4"], doc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_Cheapest_ResolvesTheShapeOncePerModelAndWorkspace()
  {
    // The workspace shape is an aggregate over the whole ledger, so a per-turn query would be
    // paid on every request of a conversation. Two consecutive turns reuse one resolution.
    FakeUsageQueries usage = new(WorkspaceShape);
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = await req.Content!.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog(TwoPricedEndpoints()), usageQueries: usage)
    { WorkspaceId = "ws-1" };

    _ = await provider.SendAsync(Model(ModelVariant.Cheapest), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await provider.SendAsync(Model(ModelVariant.Cheapest), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(1, usage.WorkspaceShapeCalls);
    Assert.Equal(1, usage.PerformanceCalls);
    using JsonDocument doc = JsonDocument.Parse(captured!);
    Assert.Equal(["a/fp4", "b/fp8"], doc.RootElement.GetProperty("provider")
        .GetProperty("order").EnumerateArray().Select(e => e.GetString()));
  }

  [Fact]
  public async Task Body_Cheapest_ResolvesAfreshForADifferentModel()
  {
    FakeUsageQueries usage = new(WorkspaceShape);
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Ok()));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog(TwoPricedEndpoints()), usageQueries: usage)
    { WorkspaceId = "ws-1" };

    _ = await provider.SendAsync(Model(ModelVariant.Cheapest), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await provider.SendAsync(Model(ModelVariant.Cheapest, "beta/other"), Request(),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2, usage.PerformanceCalls);
  }

  private static ModelConfig Model(ModelVariant variant, string modelId = BaseModel)
      => ModelConfig.Create(modelId, 64, 0.7f, 4096, variant: variant,
          retention: RetentionPolicy.RetentionAndTraining).Value!;

  private static ModelRequest Request()
      => new([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]);

  private static async Task<string> CaptureBodyAsync(ModelVariant variant,
      IReadOnlyList<ModelProviderEntry> endpoints, FakeUsageQueries? usage,
      RetentionPolicy retention = RetentionPolicy.RetentionAndTraining,
      IReadOnlyList<string>? eligible = null)
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config,
        catalog: new FakeCatalog(endpoints, eligible), usageQueries: usage)
    { WorkspaceId = "ws-1" };

    ModelConfig config = ModelConfig.Create(BaseModel, 64, 0.7f, 4096, variant: variant,
        retention: retention).Value!;
    _ = await provider.SendAsync(config, Request(), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    return captured!;
  }

  private sealed class FakeCatalog(IReadOnlyList<ModelProviderEntry> entries,
      IReadOnlyList<string>? eligible = null) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success(entries));

    public Task<RetentionEligibleList?> GetRetentionEligibleAsync(CancellationToken ct = default)
        => Task.FromResult(eligible is null
            ? null
            : RetentionEligibleList.Of(eligible.Select(slug =>
                new RetentionEligibleEndpoint(BaseModel, "Provider", slug, false))));
  }

  private sealed class FailingCatalog : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
            new DomainError("CatalogUnavailable", "the catalog could not be read.")));
  }

  /// <summary>The usage ledger as the provider sees it, counting the reads so the caching
  ///     behaviour is observable rather than asserted.</summary>
  private sealed class FakeUsageQueries(CostShape? workspaceShape) : IUsageQueries
  {
    public bool FailAll { get; init; }

    /// <summary>The report the performance query answers with, or null for an empty one.</summary>
    public ProviderPerformanceReport? Report { get; init; }

    public int WorkspaceShapeCalls { get; private set; }

    public int PerformanceCalls { get; private set; }

    public Task<Result<UsageSummary>> SessionSummaryAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(UsageSummary.Empty));

    public Task<Result<ProviderPerformanceReport>> ProviderPerformanceAsync(
        ProviderPerformanceRequest request, CancellationToken ct = default)
    {
      PerformanceCalls++;
      return Task.FromResult(FailAll
          ? Result.Failure<ProviderPerformanceReport>(new DomainError("QueryFailed", "unreadable."))
          : Result.Success(Report ?? ProviderPerformanceReport.Empty(UsageScope.Workspace)));
    }

    public Task<Result<CostShape?>> WorkspaceShapeAsync(CancellationToken ct = default)
    {
      WorkspaceShapeCalls++;
      return Task.FromResult(FailAll
          ? Result.Failure<CostShape?>(new DomainError("QueryFailed", "unreadable."))
          : Result.Success(workspaceShape));
    }
  }
}
#pragma warning restore CA2000
