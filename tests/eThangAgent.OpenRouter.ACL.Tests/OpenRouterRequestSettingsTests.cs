using System.Text.Json;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterRequestSettingsTests
{
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
    "openrouter:subagent",
    "openrouter:search_models",
    "openrouter:tool_search"
  ];

  private static readonly string[] ServerToolBudgetWireKeys =
  [
    "max_tool_calls",
    "stop_server_tools_when"
  ];

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
      Bash: true,
      Fusion: true,
      Advisor: true,
      Subagent: true,
      SearchModels: true,
      ToolSearch: true,
      MaxToolCalls: 5,
      StopServerToolsWhen: ["number_of_tool_calls"]),
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
  public void Parse_RoundTripsFullInstance()
  {
    OpenRouterRequestSettings settings = FullyPopulated();

    OpenRouterRequestSettings? parsed = OpenRouterRequestSettings.Parse(OpenRouterRequestSettings.Serialize(settings));

    Assert.NotNull(parsed);
    Assert.Equal(settings, parsed);
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
}
