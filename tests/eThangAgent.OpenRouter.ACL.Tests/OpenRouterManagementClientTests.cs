using System.Net;
using eThangAgent.ToolDomain;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The management client's wire contract over a fake handler: request
///     method/URL/auth/body per command, response parsing per the documented
///     shapes, and typed failures carrying the capped error body.</summary>
public class OpenRouterManagementClientTests
{
  private static OpenRouterConfiguration Config =>
      new("sk-or-model-key", new Uri("https://openrouter.test"), ManagementKey: "sk-or-mng-1");

  private static OpenRouterManagementClient Client(FakeHttpMessageHandler handler) =>
      new(new HttpClient(handler), Config);

  private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
      new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

  private const string GenerationJson =
      /*lang=json,strict*/ "{\"id\":\"gen-1\",\"model\":\"openai/gpt-4.1\",\"provider\":\"OpenAI\",\"route\":\"fallback\",\"created\":\"2024-07-15T23:33:19.409Z\",\"streamed\":true,\"cancelled\":false,\"finish_reason\":\"stop\",\"native_finish_reason\":\"stop\",\"strategy\":\"priority\",\"tokens_prompt\":10,\"tokens_completion\":25,\"tokens_reasoning\":5,\"tokens_cached\":3,\"cost\":0.0015,\"usage\":0.0015,\"latency\":1250,\"moderation_latency\":300,\"generation_time\":800,\"upstream_error\":false,\"upstream_id\":\"up-1\",\"request_id\":\"req-1\",\"seed\":12345,\"external_user\":\"exa\",\"endpoint\":\"/v1/responses\",\"upstream_transport\":\"http\"}";

  private static string Data(string inner) => "{\"data\":" + inner + "}";

  // ---- request shape ----

  [Fact]
  public async Task Generation_Sends_Get_With_Bearer_Management_Key()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK, Data(GenerationJson)));
    });
    OpenRouterManagementClient client = Client(handler);

    OpenRouterManagementOutcome outcome = await client.ExecuteAsync(
        new OpenRouterManagementCommand.GetGeneration("gen-1"), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal(HttpMethod.Get, sent.Method);
    Assert.Equal("https://openrouter.test/api/v1/generation?id=gen-1", sent.RequestUri!.ToString());
    Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
    Assert.Equal("sk-or-mng-1", sent.Headers.Authorization.Parameter);
    OpenRouterManagementOutcome.Generation generation = Assert.IsType<OpenRouterManagementOutcome.Generation>(outcome);
    Assert.Equal("gen-1", generation.Item.Id);
    Assert.Equal("openai/gpt-4.1", generation.Item.Model);
    Assert.Equal("OpenAI", generation.Item.ProviderName);
    Assert.Equal("stop", generation.Item.FinishReason);
    Assert.Equal(10, generation.Item.PromptTokens);
    Assert.Equal(25, generation.Item.CompletionTokens);
    Assert.Equal(0.0015, generation.Item.TotalCost);
    Assert.Equal(1250, generation.Item.Latency);
    Assert.True(generation.Item.Streamed);
  }

  [Fact]
  public async Task Generation_Content_Sends_The_Content_Route()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":{\"error\":null,\"input\":{\"messages\":[{\"content\":\"What is the meaning of life?\",\"role\":\"user\"}]},\"output\":{\"content\":\"42\"},\"url\":\"https://openrouter.ai/api/v1/generation?id=gen-1&content=true\"}}"));
    });
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetGenerationContent("gen-1"), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("https://openrouter.test/api/v1/generation/content?id=gen-1", sent.RequestUri!.ToString());
    OpenRouterManagementOutcome.GenerationContent content = Assert.IsType<OpenRouterManagementOutcome.GenerationContent>(outcome);
    Assert.Null(content.Item.Error);
    Assert.Contains("meaning of life", content.Item.Input, StringComparison.Ordinal);
    Assert.Contains("42", content.Item.Output, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Credits_Sends_The_Credits_Route_And_Parses_The_Balance()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":{\"total_credits\":100.5,\"total_usage\":25.75}}"));
    });
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("https://openrouter.test/api/v1/credits", sent.RequestUri!.ToString());
    OpenRouterManagementOutcome.Credits credits = Assert.IsType<OpenRouterManagementOutcome.Credits>(outcome);
    Assert.Equal(100.5, credits.Item.TotalCredits);
    Assert.Equal(25.75, credits.Item.TotalUsage);
  }

  [Fact]
  public async Task Activity_Sends_Filters_And_Parses_Rows()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":[{\"date\":\"2025-08-24\",\"model\":\"openai/gpt-4.1\",\"provider_name\":\"OpenAI\",\"request_count\":5,\"tokens_prompt\":50,\"tokens_completion\":125,\"tokens_reasoning\":25,\"tokens_cached\":10,\"usage\":0.015,\"byok_usage\":0.012}]}"));
    });
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetActivity("2025-08-24", "workspace"), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("https://openrouter.test/api/v1/activity?date=2025-08-24&group_by=workspace", sent.RequestUri!.ToString());
    OpenRouterManagementOutcome.Activity activity = Assert.IsType<OpenRouterManagementOutcome.Activity>(outcome);
    OpenRouterActivityRow row = Assert.Single(activity.Rows);
    Assert.Equal("2025-08-24", row.Date);
    Assert.Equal("openai/gpt-4.1", row.Model);
    Assert.Equal(5, row.RequestCount);
    Assert.Equal(0.015, row.Usage);
    Assert.Equal(0.012, row.ByokUsage);
  }

  [Fact]
  public async Task Analytics_Meta_Sends_The_Meta_Route()
  {
    List<HttpRequestMessage> seen = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      seen.Add(req);
      return Task.FromResult(Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":{\"metrics\":[{\"name\":\"request_count\",\"label\":\"Request Count\",\"type\":\"number\"}],\"dimensions\":[{\"name\":\"model\",\"label\":\"Model\"}],\"granularities\":[{\"name\":\"day\",\"label\":\"Day\"}]}}"));
    });
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetAnalyticsMeta(), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal("https://openrouter.test/api/v1/analytics/meta", sent.RequestUri!.ToString());
    OpenRouterManagementOutcome.AnalyticsMeta meta = Assert.IsType<OpenRouterManagementOutcome.AnalyticsMeta>(outcome);
    OpenRouterAnalyticsField metric = Assert.Single(meta.Metrics);
    Assert.Equal("request_count", metric.Name);
    Assert.Equal("Request Count", metric.Label);
    Assert.Equal("number", metric.Type);
    _ = Assert.Single(meta.Dimensions);
    _ = Assert.Single(meta.Granularities);
  }

  [Fact]
  public async Task Analytics_Query_Posts_The_Body_And_Parses_Rows()
  {
    List<HttpRequestMessage> seen = [];
    List<string> bodies = [];
    FakeHttpMessageHandler handler = new(async req =>
    {
      seen.Add(req);
      bodies.Add(req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      return Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":{\"rows\":[{\"date__day\":\"2025-01-01T00:00:00.000Z\",\"request_count\":\"1500\"}],\"row_count\":1,\"truncated\":false,\"next_offset\":42}}");
    });
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.QueryAnalytics(["request_count"], ["model"], "day",
            "2025-01-01T00:00:00Z", "2025-01-08T00:00:00Z", 50, 100), TestContext.Current.CancellationToken);

    HttpRequestMessage sent = Assert.Single(seen);
    Assert.Equal(HttpMethod.Post, sent.Method);
    Assert.Equal("https://openrouter.test/api/v1/analytics/query", sent.RequestUri!.ToString());
    string body = Assert.Single(bodies);
    Assert.Contains("\"metrics\":[\"request_count\"]", body, StringComparison.Ordinal);
    Assert.Contains("\"dimensions\":[\"model\"]", body, StringComparison.Ordinal);
    Assert.Contains("\"granularity\":\"day\"", body, StringComparison.Ordinal);
    Assert.Contains("\"time_range_start\":\"2025-01-01T00:00:00Z\"", body, StringComparison.Ordinal);
    Assert.Contains("\"group_limit\":50", body, StringComparison.Ordinal);
    Assert.Contains("\"limit\":100", body, StringComparison.Ordinal);
    OpenRouterManagementOutcome.Analytics analytics = Assert.IsType<OpenRouterManagementOutcome.Analytics>(outcome);
    Assert.Equal(1, analytics.Result.RowCount);
    Assert.False(analytics.Result.Truncated);
    Assert.Equal(42, analytics.Result.NextOffset);
    Assert.Equal("1500", Assert.Single(analytics.Result.Rows)["request_count"]);
  }

  [Fact]
  public async Task Analytics_Query_Omits_Unprovided_Optionals()
  {
    List<string> bodies = [];
    FakeHttpMessageHandler handler = new(async req =>
    {
      bodies.Add(req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      return Json(HttpStatusCode.OK,
          /*lang=json,strict*/ "{\"data\":{\"rows\":[],\"row_count\":0,\"truncated\":false}}");
    });
    _ = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.QueryAnalytics(["request_count"], null, null, null, null, null, null),
        TestContext.Current.CancellationToken);
    string body = Assert.Single(bodies);
    Assert.Equal(/*lang=json,strict*/ "{\"metrics\":[\"request_count\"]}", body);
  }

  // ---- failures ----

  [Fact]
  public async Task Http_Error_Carries_The_Capped_Body()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(Json(HttpStatusCode.Unauthorized, /*lang=json,strict*/ "{\"error\":{\"message\":\"invalid key\"}}")));
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);
    OpenRouterManagementOutcome.Failure failure = Assert.IsType<OpenRouterManagementOutcome.Failure>(outcome);
    Assert.Equal("ManagementError", failure.Code);
    Assert.Contains("401", failure.Message, StringComparison.Ordinal);
    Assert.Contains("invalid key", failure.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unparseable_Success_Body_Is_A_Parse_Error()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Json(HttpStatusCode.OK, "not json")));
    OpenRouterManagementOutcome outcome = await Client(handler).ExecuteAsync(
        new OpenRouterManagementCommand.GetCredits(), TestContext.Current.CancellationToken);
    OpenRouterManagementOutcome.Failure failure = Assert.IsType<OpenRouterManagementOutcome.Failure>(outcome);
    Assert.Equal("ManagementParseError", failure.Code);
  }
}
