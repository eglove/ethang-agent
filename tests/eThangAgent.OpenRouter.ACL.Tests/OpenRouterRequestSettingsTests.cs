// The expected wire-JSON literal below is deliberate: the plugins shape contract
// is asserted byte-for-byte against the serializer's output.
#pragma warning disable JSON002
using System.Text.Json;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterRequestSettingsTests
{
  /// <summary>Every server-tool wire type string EXCEPT web_search (used as the
  ///     enabled-tool subject of the exclusion assertions) — including
  ///     openrouter:bash, which must never appear: the tool does not exist on the
  ///     responses API.</summary>
  private static readonly string[] OtherServerToolWireTypes =
  [
    "openrouter:web_fetch",
    "openrouter:datetime",
    "openrouter:image_generation",
    "openrouter:shell",
    "openrouter:apply_patch",
    "openrouter:bash",
    "openrouter:fusion",
    "openrouter:advisor",
    "openrouter:experimental__search_models",
    "openrouter:tool_search"
  ];

  /// <summary>All ten server tools' wire type strings, in declared order.</summary>
  private static readonly string[] AllServerToolWireTypes =
  [
    "openrouter:web_search",
    "openrouter:web_fetch",
    "openrouter:datetime",
    "openrouter:image_generation",
    "openrouter:shell",
    "openrouter:apply_patch",
    "openrouter:fusion",
    "openrouter:advisor",
    "openrouter:experimental__search_models",
    "openrouter:tool_search"
  ];

  private static readonly string[] ServerToolBudgetWireKeys =
  [
    "max_tool_calls",
    "stop_server_tools_when"
  ];

  private static ServerToolStopCondition Condition(string type, object? value) =>
      ServerToolStopCondition.Create(type, value).Value!;

  private static OpenRouterRequestSettings FullyPopulated() => new()
  {
    Routing = new Routing(
      Order: ["openai/gpt-5", "anthropic/claude-sonnet-4.5"],
      Only: ["openai"],
      Ignore: ["google"],
      AllowFallbacks: false,
      Sort: "price",
      Quantizations: ["int4", "int8"],
      RequireParameters: true,
      DataCollection: "deny",
      Models: ["openai/gpt-5"],
      Route: "fallback"),
    ServerTools = new ServerTools(
      WebSearch: true,
      WebFetch: true,
      Datetime: true,
      ImageGeneration: true,
      Shell: true,
      ApplyPatch: true,
      Fusion: true,
      Advisor: true,
      SearchModels: true,
      ToolSearch: true,
      MaxToolCalls: 5,
      StopServerToolsWhen:
      [
        Condition(ServerToolStopCondition.StepCountIs, 12),
        Condition(ServerToolStopCondition.HasToolCall, "web_search"),
        Condition(ServerToolStopCondition.MaxTokensUsed, 4096),
        Condition(ServerToolStopCondition.MaxCost, 0.5m),
        Condition(ServerToolStopCondition.FinishReasonIs, "stop"),
      ]),
    Plugins = new Plugins(
      WebGrounding: true,
      ResponseHealing: true,
      WebGroundingEngine: "exa")
  };

  [Fact]
  public void Serialize_AllDefault_SerializesToEmptyObject()
  {
    Assert.Null(OpenRouterRequestSettings.Serialize(null));

    string emptyObject = JsonSerializer.Serialize(new { });
    Assert.Equal(emptyObject, OpenRouterRequestSettings.Serialize(new OpenRouterRequestSettings()));
  }

  [Fact]
  public void Serialize_EveryEnabledTool_EmitsAllTenWireTypesAndNoBash()
  {
    OpenRouterRequestSettings settings = new()
    {
      ServerTools = new ServerTools(
        WebSearch: true, WebFetch: true, Datetime: true, ImageGeneration: true, Shell: true,
        ApplyPatch: true, Fusion: true, Advisor: true, SearchModels: true,
        ToolSearch: true)
    };

    string json = OpenRouterRequestSettings.Serialize(settings)!;

    foreach (string wireType in AllServerToolWireTypes)
    {
      Assert.Contains(wireType, json, StringComparison.Ordinal);
    }

    // openrouter:bash was removed with the responses-API migration: it is not one of
    // the ten tools and is never serialized, even fully populated.
    Assert.DoesNotContain("openrouter:bash", json, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_EnabledWebSearchOnly_ContainsWebSearchEntry()
  {
    OpenRouterRequestSettings settings = new() { ServerTools = new ServerTools(WebSearch: true) };

    string json = OpenRouterRequestSettings.Serialize(settings)!;

    Assert.Contains("openrouter:web_search", json, StringComparison.Ordinal);
    foreach (string other in OtherServerToolWireTypes)
    {
      Assert.DoesNotContain(other, json, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Serialize_EnabledSearchModelsOnly_ContainsWireTypeEntry()
  {
    OpenRouterRequestSettings settings = new() { ServerTools = new ServerTools(SearchModels: true) };

    string json = OpenRouterRequestSettings.Serialize(settings)!;

    // Controller ruling (docs-authoritative): the SearchModels toggle rides the
    // experimental__ wire type string OpenRouter's docs declare, never the bare one.
    Assert.Contains("openrouter:experimental__search_models", json, StringComparison.Ordinal);
    Assert.DoesNotContain("openrouter:search_models", json, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_RoundTripsFullInstance()
  {
    OpenRouterRequestSettings settings = FullyPopulated();

    OpenRouterRequestSettings? parsed = OpenRouterRequestSettings.Parse(OpenRouterRequestSettings.Serialize(settings));

    Assert.NotNull(parsed);
    Assert.Equal(settings, parsed);
  }

  [Fact]
  public void Parse_StopConditions_RoundTripAsDiscriminatedUnionObjects()
  {
    const string json = /*lang=json,strict*/
        """{"server_tools":{"stop_server_tools_when":[{"type":"step_count_is","step_count":3},{"type":"has_tool_call","tool_name":"web_search"},{"type":"max_tokens_used","max_tokens":1000},{"type":"max_cost","max_cost_in_dollars":1.5},{"type":"finish_reason_is","reason":"length"}]}}""";

    OpenRouterRequestSettings? parsed = OpenRouterRequestSettings.Parse(json);

    Assert.NotNull(parsed);
    ServerToolStopCondition[] conditions = parsed.ServerTools.StopServerToolsWhen!;
    Assert.Equal(5, conditions.Length);
    Assert.Equal(ServerToolStopCondition.StepCountIs, conditions[0].Type);
    Assert.Equal(3, conditions[0].StepCount);
    Assert.Equal(ServerToolStopCondition.HasToolCall, conditions[1].Type);
    Assert.Equal("web_search", conditions[1].ToolName);
    Assert.Equal(ServerToolStopCondition.MaxTokensUsed, conditions[2].Type);
    Assert.Equal(1000, conditions[2].MaxTokens);
    Assert.Equal(ServerToolStopCondition.MaxCost, conditions[3].Type);
    Assert.Equal(1.5m, conditions[3].MaxCostInDollars);
    Assert.Equal(ServerToolStopCondition.FinishReasonIs, conditions[4].Type);
    Assert.Equal("length", conditions[4].FinishReason);
  }

  [Fact]
  public void Parse_UnknownConditionType_ThrowsJsonException()
    => Assert.Throws<JsonException>(() => OpenRouterRequestSettings.Parse(
        /*lang=json,strict*/ """{"server_tools":{"stop_server_tools_when":[{"type":"mystery_condition","count":1}]}}"""));

  [Fact]
  public void Parse_ConditionMissingItsField_ThrowsJsonException()
    => Assert.Throws<JsonException>(() => OpenRouterRequestSettings.Parse(
        /*lang=json,strict*/ """{"server_tools":{"stop_server_tools_when":[{"type":"step_count_is"}]}}"""));

  [Fact]
  public void Parse_PersistedBashToggle_IsSkippedAsUnknownKey()
  {
    // Settings persisted before the responses-API migration may carry
    // "openrouter:bash": true inside server_tools. The key no longer names a member:
    // unknown keys are skipped on read (the surface grows server-side without
    // breaking old readers), so the load succeeds and bash is simply not enabled.
    const string json = /*lang=json,strict*/
        """{"server_tools":{"openrouter:bash":true,"openrouter:web_search":true,"openrouter:datetime":true}}""";

    OpenRouterRequestSettings? parsed = OpenRouterRequestSettings.Parse(json);

    Assert.NotNull(parsed);
    Assert.True(parsed.ServerTools.WebSearch);
    Assert.True(parsed.ServerTools.Datetime);
    Assert.DoesNotContain("openrouter:bash", OpenRouterRequestSettings.Serialize(parsed), StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_NullOrWhitespace_ReturnsNull()
  {
    Assert.Null(OpenRouterRequestSettings.Parse(null));
    Assert.Null(OpenRouterRequestSettings.Parse(string.Empty));
    Assert.Null(OpenRouterRequestSettings.Parse("   "));
  }

  [Fact]
  public void Parse_Malformed_ThrowsJsonException()
    => Assert.Throws<JsonException>(() => OpenRouterRequestSettings.Parse("not json{"));

  [Fact]
  public void Serialize_OmitsFalseTogglesAndNullBudgets()
  {
    string json = OpenRouterRequestSettings.Serialize(new OpenRouterRequestSettings())!;

    foreach (string budgetKey in ServerToolBudgetWireKeys)
    {
      Assert.DoesNotContain(budgetKey, json, StringComparison.Ordinal);
    }
    Assert.DoesNotContain("openrouter:web_search", json, StringComparison.Ordinal);
    foreach (string other in OtherServerToolWireTypes)
    {
      Assert.DoesNotContain(other, json, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void Serialize_WebGroundingWithEngine_EmitsEngineExactlyOnceInsideWebEntry()
  {
    OpenRouterRequestSettings settings = new() { Plugins = new Plugins(WebGrounding: true, WebGroundingEngine: "exa") };

    string json = OpenRouterRequestSettings.Serialize(settings)!;

    // Pins the plugins wire shape: the engine value appears exactly once, inside
    // the web entry, and no plugins-level engine key leaks beside it.
    Assert.Equal("{\"plugins\":{\"web\":{\"engine\":\"exa\"}}}", json);
  }

  [Fact]
  public void Parse_JsonNull_ThrowsJsonException()
    => Assert.Throws<JsonException>(() => OpenRouterRequestSettings.Parse("null"));

#pragma warning restore JSON002
}
