using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>Request-body wire tests for the full configurable surface: the neutral
///     sampling knobs, provider routing, server-side tools, server-tool budgets, and
///     plugins — every wire shape asserted here follows the OpenRouter API contract.</summary>
public class OpenRouterModelProviderRequestTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static readonly string[] KnobWireKeys =
  [
    "top_p",
    "top_k",
    "frequency_penalty",
    "presence_penalty",
    "repetition_penalty",
    "min_p",
    "top_a",
    "seed",
    "verbosity",
    "parallel_tool_calls",
  ];

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
  {
    Content = new StringContent(
        /*lang=json,strict*/
        """{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
  };

  private static async Task<string> CaptureBodyAsync(ModelConfig config, ModelRequest? request = null)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        config, request ?? new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken).ConfigureAwait(true);

    return capturedBody!;
  }

  private static ModelConfig WithSettings(OpenRouterRequestSettings settings, string? provider = null)
    => ModelConfig.Create(
        "openai/gpt-5", provider, 64, 0.7f, 4096,
        providerSettings: OpenRouterRequestSettings.Serialize(settings)).Value!;

  [Fact]
  public async Task Body_OmitsEveryNullKnob()
  {
    string body = await CaptureBodyAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")])).ConfigureAwait(true);

    foreach (string key in KnobWireKeys)
    {
      Assert.DoesNotContain($"\"{key}\"", body, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task Body_SendsEverySetKnob()
  {
    string body = await CaptureBodyAsync(
        ModelConfig.Create(
            "openai/gpt-5", null, 64, 0.7f, 4096,
            topP: 0.9f,
            topK: 40,
            frequencyPenalty: 0.5f,
            presencePenalty: -0.5f,
            repetitionPenalty: 1.1f,
            minP: 0.05f,
            topA: 0.8f,
            seed: 42,
            verbosity: VerbosityLevel.High,
            parallelToolCalls: false).Value!,
        new ModelRequest([UserMsg("hi")])).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement root = doc.RootElement;
    Assert.Equal(0.9f, root.GetProperty("top_p").GetSingle());
    Assert.Equal(40, root.GetProperty("top_k").GetInt32());
    Assert.Equal(0.5f, root.GetProperty("frequency_penalty").GetSingle());
    Assert.Equal(-0.5f, root.GetProperty("presence_penalty").GetSingle());
    Assert.Equal(1.1f, root.GetProperty("repetition_penalty").GetSingle());
    Assert.Equal(0.05f, root.GetProperty("min_p").GetSingle());
    Assert.Equal(0.8f, root.GetProperty("top_a").GetSingle());
    Assert.Equal(42, root.GetProperty("seed").GetInt32());
    Assert.Equal("high", root.GetProperty("verbosity").GetString());
    Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
  }

  [Fact]
  public async Task Body_RoutingReplacesProviderObject()
  {
    OpenRouterRequestSettings settings = new()
    {
      Routing = new Routing(Order: ["a", "b"], Sort: "price", AllowFallbacks: false)
    };
    string body = await CaptureBodyAsync(WithSettings(settings, provider: "legacy-pin")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    string[] order = [.. provider.GetProperty("order").EnumerateArray().Select(e => e.GetString()!)];
    Assert.Equal(["a", "b"], order);
    Assert.Equal("price", provider.GetProperty("sort").GetString());
    Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
    Assert.False(provider.TryGetProperty("only", out _));
  }

  [Fact]
  public async Task Body_ProviderPinningPreserved_WithoutRouting()
  {
    string body = await CaptureBodyAsync(WithSettings(new OpenRouterRequestSettings(), provider: "openai")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement provider = doc.RootElement.GetProperty("provider");
    JsonElement only = provider.GetProperty("only");
    _ = Assert.Single(only.EnumerateArray());
    Assert.Equal("openai", only[0].GetString());
  }

  [Fact]
  public async Task Body_ServerToolsAppendOpenRouterEntries()
  {
    OpenRouterRequestSettings settings = new()
    {
      ServerTools = new ServerTools(WebSearch: true, Datetime: true, SearchModels: true)
    };
    List<ToolDefinition> tools =
      [
        new("demo_tool", "desc",
        [
          new ToolParameter("options", ToolParameterType.Text, "opts"),
        ]),
      ];
    string body = await CaptureBodyAsync(
        WithSettings(settings), new ModelRequest([UserMsg("hi")], tools)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement toolsElement = doc.RootElement.GetProperty("tools");
    Assert.Equal(4, toolsElement.GetArrayLength());
    Assert.Equal("openrouter:web_search", toolsElement[0].GetProperty("type").GetString());
    Assert.Equal("openrouter:datetime", toolsElement[1].GetProperty("type").GetString());
    Assert.Equal("openrouter:experimental__search_models", toolsElement[2].GetProperty("type").GetString());
    _ = Assert.Single(toolsElement[0].EnumerateObject());
    Assert.Equal("demo_tool", toolsElement[3].GetProperty("function").GetProperty("name").GetString());
  }

  [Fact]
  public async Task Body_Budgets()
  {
    string withBudget = await CaptureBodyAsync(WithSettings(
        new OpenRouterRequestSettings
        {
          ServerTools = new ServerTools(WebSearch: true, MaxToolCalls: 7)
        })).ConfigureAwait(true);
    using (JsonDocument doc = JsonDocument.Parse(withBudget))
    {
      Assert.Equal(7, doc.RootElement.GetProperty("max_tool_calls").GetInt32());
    }

    string withoutBudget = await CaptureBodyAsync(WithSettings(new OpenRouterRequestSettings())).ConfigureAwait(true);
    using (JsonDocument doc = JsonDocument.Parse(withoutBudget))
    {
      Assert.False(doc.RootElement.TryGetProperty("max_tool_calls", out _));
      Assert.False(doc.RootElement.TryGetProperty("stop_server_tools_when", out _));
    }

    string withStop = await CaptureBodyAsync(WithSettings(
        new OpenRouterRequestSettings
        {
          ServerTools = new ServerTools(StopServerToolsWhen: ["number_of_tool_calls"])
        })).ConfigureAwait(true);
    using (JsonDocument doc = JsonDocument.Parse(withStop))
    {
      JsonElement stop = doc.RootElement.GetProperty("stop_server_tools_when");
      Assert.Equal(1, stop.GetArrayLength());
      Assert.Equal("number_of_tool_calls", stop[0].GetString());
    }
  }

  [Fact]
  public async Task Body_Plugins()
  {
    string body = await CaptureBodyAsync(WithSettings(
        new OpenRouterRequestSettings
        {
          Plugins = new Plugins(WebGrounding: true, WebGroundingEngine: "exa", ResponseHealing: true)
        })).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement plugins = doc.RootElement.GetProperty("plugins");
    Assert.Equal(2, plugins.GetArrayLength());
    Assert.Equal("web", plugins[0].GetProperty("id").GetString());
    Assert.Equal("exa", plugins[0].GetProperty("engine").GetString());
    Assert.Equal("response-healing", plugins[1].GetProperty("id").GetString());
    Assert.False(plugins[1].TryGetProperty("engine", out _));
  }

  [Fact]
  public async Task Body_AllDefaultSettings_ChangesNothing()
  {
    string withDefaults = await CaptureBodyAsync(WithSettings(new OpenRouterRequestSettings())).ConfigureAwait(true);
    string withoutSettings = await CaptureBodyAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!).ConfigureAwait(true);

    Assert.Equal(withoutSettings, withDefaults);
  }

  // Corrupt ProviderSettings is an expected environmental failure: it flows through
  // the provider's Result error contract (a named DomainError), never as an exception
  // — no catch set on the send path would otherwise intercept it.
  private const string CorruptSettings = "{not json";

  private static ModelConfig WithCorruptSettings() =>
      ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096, providerSettings: CorruptSettings).Value!;

  [Fact]
  public async Task Send_CorruptSettings_FailsThroughResultContract()
  {
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        WithCorruptSettings(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidProviderSettings", result.Error.Code);
    Assert.Contains("provider settings", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task SendStreaming_CorruptSettings_FailsThroughResultContract()
  {
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(
        WithCorruptSettings(), new ModelRequest([UserMsg("hi")]),
        onContentDelta: null, onReasoningDelta: null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidProviderSettings", result.Error.Code);
    Assert.Contains("provider settings", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }
}
