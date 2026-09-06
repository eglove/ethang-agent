using eThangAgent.AgentDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The settings record is the host-facing credential seam: preference keys
///     name where hosts persist keys, and WithApiKeys overlays them without touching
///     the rest of the configuration.</summary>
public class AgentSettingsTests
{
  private static AgentSettings Settings(string? openRouter = null, string? zai = null) => new(
      new OpenRouterSettings(openRouter, new Uri("https://openrouter.test")),
      new ZaiSettings(zai, new Uri("https://zai.test")),
      new SubAgentOptions(null, 2));

  [Fact]
  public void Preference_Keys_Name_The_Stored_Api_Key_Slots()
  {
    Assert.Equal("openrouter_api_key", OpenRouterSettings.PreferenceKey);
    Assert.Equal("zai_api_key", ZaiSettings.PreferenceKey);
  }

  [Fact]
  public void WithApiKeys_Overlays_Both_Keys_And_Flags()
  {
    AgentSettings overlaid = Settings().WithApiKeys("sk-or-test", "zai-test-key");

    Assert.Equal("sk-or-test", overlaid.OpenRouter.ApiKey);
    Assert.Equal("zai-test-key", overlaid.Zai.ApiKey);
    Assert.True(overlaid.HasOpenRouter);
    Assert.True(overlaid.HasZai);
    // Untouched members carry over.
    Assert.Equal(new Uri("https://openrouter.test"), overlaid.OpenRouter.BaseUrl);
    Assert.Equal(new Uri("https://zai.test"), overlaid.Zai.BaseUrl);
  }

  [Fact]
  public void WithApiKeys_Null_Clears_A_Key()
  {
    AgentSettings overlaid = Settings(openRouter: "sk-or-test").WithApiKeys(null, "zai-test-key");

    Assert.Null(overlaid.OpenRouter.ApiKey);
    Assert.False(overlaid.HasOpenRouter);
    Assert.True(overlaid.HasZai);
  }

  [Fact]
  public void WithWorkspaceRoot_Overlays_And_Never_Mutates()
  {
    AgentSettings original = Settings(openRouter: "sk-or-test");
    Assert.Null(original.WorkspaceRoot);

    AgentSettings overlaid = original.WithWorkspaceRoot("C:\\ws\\anchor");

    Assert.Equal("C:\\ws\\anchor", overlaid.WorkspaceRoot);
    Assert.Null(original.WorkspaceRoot);
    // Untouched members carry over.
    Assert.True(overlaid.HasOpenRouter);
  }

  [Fact]
  public void Settings_Json_Round_Trips_WorkspaceRoot_And_Old_Json_Reads_Null()
  {
    System.Text.Json.JsonSerializerOptions options = new(System.Text.Json.JsonSerializerDefaults.Web);
    AgentSettings settings = Settings().WithWorkspaceRoot("C:\\temp\\ws");

    string json = System.Text.Json.JsonSerializer.Serialize(settings, options);
    AgentSettings parsed = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(json, options)!;
    Assert.Equal("C:\\temp\\ws", parsed.WorkspaceRoot);

    // A settings JSON written before the member existed (no WorkspaceRoot key)
    // deserializes with a null root - the documented fallback, never a fault.
    string legacy = "{\"OpenRouter\":{\"ApiKey\":null,\"BaseUrl\":\"http://openrouter.test\"},\"Zai\":{\"ApiKey\":null,\"BaseUrl\":\"http://zai.test\"},\"SubAgents\":{\"MaxConcurrentAgents\":1}}";
    AgentSettings legacyParsed = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(legacy, options)!;
    Assert.Null(legacyParsed.WorkspaceRoot);
  }

  [Fact]
  public void WithApiKeys_Does_Not_Mutate_The_Original()
  {
    AgentSettings original = Settings(openRouter: "before");
    _ = original.WithApiKeys("after", null);

    Assert.Equal("before", original.OpenRouter.ApiKey);
    Assert.True(original.HasOpenRouter);
    Assert.False(original.HasZai);
  }
}
