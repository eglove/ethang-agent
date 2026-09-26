namespace eThangAgent.OpenRouter.ACL.Tests;

public class SubagentServerToolRemovalTests
{
  /// <summary>The openrouter:subagent server tool is removed as an option (its minutes-long
  ///     server-side agent loop cannot complete inside the request timeout, and the harness
  ///     owns subagents natively). Settings persisted while it existed must degrade to
  ///     not-enabled — the same contract as the removed openrouter:bash toggle.</summary>
  [Fact]
  public void Parse_PersistedSubagentToggle_IsSkippedAsUnknownKey()
  {
    const string json = /*lang=json,strict*/
        """{"server_tools":{"openrouter:subagent":true,"openrouter:web_search":true,"openrouter:datetime":true}}""";

    OpenRouterRequestSettings? parsed = OpenRouterRequestSettings.Parse(json);

    Assert.NotNull(parsed);
    Assert.True(parsed.ServerTools.WebSearch);
    Assert.True(parsed.ServerTools.Datetime);
    Assert.DoesNotContain("openrouter:subagent", OpenRouterRequestSettings.Serialize(parsed), StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_NeverEmitsSubagentWireType()
  {
    // No member can enable it anymore: a fully-populated ServerTools serializes without
    // the subagent wire type.
    OpenRouterRequestSettings settings = new()
    {
      ServerTools = new ServerTools(
        WebSearch: true, WebFetch: true, Datetime: true, ImageGeneration: true, Shell: true,
        ApplyPatch: true, Fusion: true, Advisor: true, SearchModels: true, ToolSearch: true)
    };

    string json = OpenRouterRequestSettings.Serialize(settings)!;

    Assert.DoesNotContain("openrouter:subagent", json, StringComparison.Ordinal);
  }
}
