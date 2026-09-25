using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>Request-body wire tests for the full configurable surface: the neutral
///     sampling knobs, provider routing, server-side tools, server-tool budgets, and
///     plugins — every wire shape asserted here follows the OpenRouter Responses API
///     contract (verified live 2026-09).</summary>
public class OpenRouterModelProviderRequestTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  /// <summary>The eleven server-tool wire type strings in their declared order.</summary>
  private static readonly string[] ServerToolWireTypesInOrder =
  [
    "openrouter:web_search",
    "openrouter:web_fetch",
    "openrouter:datetime",
    "openrouter:image_generation",
    "openrouter:shell",
    "openrouter:apply_patch",
    "openrouter:fusion",
    "openrouter:advisor",
    "openrouter:subagent",
    "openrouter:experimental__search_models",
    "openrouter:tool_search",
  ];

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

  private static async Task<string> CaptureBodyAsync(ModelConfig config, ModelRequest? request = null)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
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
  public async Task Body_AllElevenServerTools_LeadTheToolsArrayInDeclaredOrder()
  {
    OpenRouterRequestSettings settings = new()
    {
      ServerTools = new ServerTools(
        WebSearch: true, WebFetch: true, Datetime: true, ImageGeneration: true, Shell: true,
        ApplyPatch: true, Fusion: true, Advisor: true, Subagent: true, SearchModels: true,
        ToolSearch: true)
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
    Assert.Equal(12, toolsElement.GetArrayLength());
    for (int i = 0; i < ServerToolWireTypesInOrder.Length; i++)
    {
      JsonElement entry = toolsElement[i];
      Assert.Equal(ServerToolWireTypesInOrder[i], entry.GetProperty("type").GetString());
      _ = Assert.Single(entry.EnumerateObject()); // v1: the type member is the whole entry
    }

    // The user-defined tool follows, in the Responses API's FLAT function shape.
    JsonElement functionTool = toolsElement[11];
    Assert.Equal("function", functionTool.GetProperty("type").GetString());
    Assert.Equal("demo_tool", functionTool.GetProperty("name").GetString());
    // openrouter:bash does not exist on the responses API — never serialized.
    Assert.DoesNotContain("openrouter:bash", body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Body_ServerToolsPrecedeUserDefinedTools()
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
    Assert.Equal("demo_tool", toolsElement[3].GetProperty("name").GetString());
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
  }

  [Fact]
  public async Task Body_StopConditions_SerializeAsDiscriminatedUnionObjects()
  {
    OpenRouterRequestSettings settings = new()
    {
      ServerTools = new ServerTools(WebSearch: true, StopServerToolsWhen:
      [
        ServerToolStopCondition.Create(ServerToolStopCondition.StepCountIs, 3).Value!,
        ServerToolStopCondition.Create(ServerToolStopCondition.HasToolCall, "web_search").Value!,
        ServerToolStopCondition.Create(ServerToolStopCondition.MaxTokensUsed, 1000).Value!,
        ServerToolStopCondition.Create(ServerToolStopCondition.MaxCost, 0.25m).Value!,
        ServerToolStopCondition.Create(ServerToolStopCondition.FinishReasonIs, "stop").Value!,
      ])
    };
    string body = await CaptureBodyAsync(WithSettings(settings)).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement stop = doc.RootElement.GetProperty("stop_server_tools_when");
    Assert.Equal(5, stop.GetArrayLength());
    Assert.Equal(/*lang=json,strict*/ """{"type":"step_count_is","step_count":3}""", stop[0].GetRawText());
    Assert.Equal(/*lang=json,strict*/ """{"type":"has_tool_call","tool_name":"web_search"}""", stop[1].GetRawText());
    Assert.Equal(/*lang=json,strict*/ """{"type":"max_tokens_used","max_tokens":1000}""", stop[2].GetRawText());
    Assert.Equal(/*lang=json,strict*/ """{"type":"max_cost","max_cost_in_dollars":0.25}""", stop[3].GetRawText());
    Assert.Equal(/*lang=json,strict*/ """{"type":"finish_reason_is","reason":"stop"}""", stop[4].GetRawText());
  }

  [Fact]
  public async Task Send_MalformedStopCondition_FailsBeforeAnyHttpSend()
  {
    // A condition whose own field is empty parses from persisted JSON (the wire
    // parser only demands a string) but fails the provider's send-boundary
    // validation — a named InvalidProviderSettings failure, never a request to
    // the provider and never a silently dropped filter.
    const string settingsJson = /*lang=json,strict*/
        """{"server_tools":{"stop_server_tools_when":[{"type":"has_tool_call","tool_name":""}]}}""";
    ModelConfig config = ModelConfig.Create(
        "openai/gpt-5", null, 64, 0.7f, 4096, providerSettings: settingsJson).Value!;
    FakeHttpMessageHandler handler = new(_ => throw new InvalidOperationException(
        "the turn must fail before any HTTP send, not here"));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        config, new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidProviderSettings", result.Error.Code);
    Assert.Contains("Malformed OpenRouter provider settings", result.Error.Message, StringComparison.Ordinal);
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

  [Fact]
  public async Task Streaming_Body_CarriesStreamAndNoStreamOptions()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendStreamingAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.NotNull(capturedBody);
    Assert.Contains("\"stream\":true", capturedBody, StringComparison.Ordinal);
    // stream_options is a chat-completions key; the responses API has no such flag —
    // usage rides the terminal response event instead.
    Assert.DoesNotContain("stream_options", capturedBody, StringComparison.Ordinal);
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
