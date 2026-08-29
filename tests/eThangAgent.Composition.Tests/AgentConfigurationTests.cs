using eThangAgent.Zai.ACL;

namespace eThangAgent.Composition.Tests;

public class AgentConfigurationTests
{
  [Fact]
  public void Api_Key_Environment_Variables_Are_Ignored()
  {
    // Keys live in each host's credential store (the Desktop's settings modal),
    // never in configuration: hosts overlay them via AgentSettings.WithApiKeys.
    AgentSettings s = Load(env: [("OPENROUTER_API_KEY", "sk-or-test"), ("ZAI_API_KEY", "zai-test-key")]);
    Assert.Null(s.OpenRouter.ApiKey);
    Assert.Null(s.Zai.ApiKey);
    Assert.False(s.HasOpenRouter);
    Assert.False(s.HasZai);
  }

  [Fact]
  public void Base_Url_Defaults_To_OpenRouter()
  {
    AgentSettings s = Load(env: []);
    Assert.Equal(new Uri("https://openrouter.ai"), s.OpenRouter.BaseUrl);
  }

  [Fact]
  public void Zai_Base_Url_Defaults_To_Platform_Root()
  {
    AgentSettings s = Load(env: []);
    Assert.Equal(new Uri("https://api.z.ai/api"), s.Zai.BaseUrl);
  }

  [Fact]
  public void Zai_Endpoint_Mode_Defaults_To_Coding_Plan()
  {
    AgentSettings s = Load(env: []);
    Assert.Equal(ZaiEndpointMode.CodingPlan, s.Zai.EndpointMode);
  }

  [Fact]
  public void Zai_Endpoint_Mode_Empty_String_Defaults_To_Coding_Plan()
  {
    AgentSettings s = Load(env: [("ZAI_ENDPOINT_MODE", "")]);
    Assert.Equal(ZaiEndpointMode.CodingPlan, s.Zai.EndpointMode);
  }

  [Fact]
  public void Zai_Endpoint_Mode_Tokens_Are_Honored()
  {
    Assert.Equal(ZaiEndpointMode.CodingPlan, Load(env: [("ZAI_ENDPOINT_MODE", "coding")]).Zai.EndpointMode);
    Assert.Equal(ZaiEndpointMode.GeneralApi, Load(env: [("ZAI_ENDPOINT_MODE", "general")]).Zai.EndpointMode);
  }

  [Theory]
  [InlineData("Coding")]
  [InlineData("coding ")]
  [InlineData("subscription")]
  public void Invalid_Zai_Endpoint_Mode_Throws_InvalidOperationException(string value)
  {
    Exception ex = Record.Exception(() => Load(env: [("ZAI_ENDPOINT_MODE", value)]));
    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("ZAI_ENDPOINT_MODE", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Base_Url_Overrides_Are_Honored()
  {
    const string loopback = "http://localhost:5599"; // devskim: ignore DS162092 - loopback override is the behavior under test
    AgentSettings s = Load(env: [("OPENROUTER_BASE_URL", loopback)]);
    Assert.Equal(new Uri(loopback), s.OpenRouter.BaseUrl);
  }

  [Fact]
  public void Zai_Base_Url_Override_Is_Honored()
  {
    const string loopback = "http://localhost:5598"; // devskim: ignore DS162092 - loopback override is the behavior under test
    AgentSettings s = Load(env: [("ZAI_BASE_URL", loopback)]);
    Assert.Equal(new Uri(loopback), s.Zai.BaseUrl);
  }

  [Fact]
  public void Invalid_Base_Url_Throws_InvalidOperationException()
  {
    Exception ex = Record.Exception(() => Load(env: [("OPENROUTER_BASE_URL", "not-a-url")]));
    _ = Assert.IsType<InvalidOperationException>(ex);
  }

  [Fact]
  public void Invalid_Zai_Base_Url_Throws_InvalidOperationException()
  {
    Exception ex = Record.Exception(() => Load(env: [("ZAI_BASE_URL", "not-a-url")]));
    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("ZAI_BASE_URL", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Invalid_SubAgent_Configuration_Throws()
  {
    Exception ex = Record.Exception(() => Load(env: [("SubAgent__MaxConcurrentAgents", "0")]));
    _ = Assert.IsType<InvalidOperationException>(ex);
  }

  // Agent:MaxToolIterations was removed with the iteration cap: an unknown key is
  // simply ignored by configuration, never a startup error.

  private static AgentSettings Load(params (string Key, string Value)[] env)
  {
    foreach ((string? key, string? value) in env)
    {
      Environment.SetEnvironmentVariable(key, value);
    }

    try
    {
      return AgentConfiguration.Load();
    }
    finally
    {
      foreach ((string? key, string _) in env)
      {
        Environment.SetEnvironmentVariable(key, null);
      }
    }
  }
}
