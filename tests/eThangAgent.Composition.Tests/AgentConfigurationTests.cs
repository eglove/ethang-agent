namespace eThangAgent.Composition.Tests;

public class AgentConfigurationTests
{
  [Fact]
  public void Missing_Api_Keys_Are_Null_Not_Throw()
  {
    AgentSettings s = Load(env: []);
    Assert.Null(s.OpenRouter.ApiKey);
    Assert.Null(s.Zai.ApiKey);
    Assert.False(s.HasOpenRouter);
    Assert.False(s.HasZai);
  }

  [Fact]
  public void OpenRouter_Api_Key_Is_Read_From_Environment()
  {
    AgentSettings s = Load(env: [("OPENROUTER_API_KEY", "sk-or-test")]);
    Assert.Equal("sk-or-test", s.OpenRouter.ApiKey);
    Assert.True(s.HasOpenRouter);
    Assert.False(s.HasZai);
  }

  [Fact]
  public void Zai_Api_Key_Is_Read_From_Environment()
  {
    AgentSettings s = Load(env: [("ZAI_API_KEY", "zai-test-key")]);
    Assert.Equal("zai-test-key", s.Zai.ApiKey);
    Assert.True(s.HasZai);
    Assert.False(s.HasOpenRouter);
  }

  [Fact]
  public void Blank_Api_Keys_Count_As_Unconfigured()
  {
    AgentSettings s = Load(env: [("OPENROUTER_API_KEY", "  ")]);
    Assert.False(s.HasOpenRouter);
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
    Exception ex = Record.Exception(() => Load(env: [
        ("OPENROUTER_API_KEY", "k"), ("OPENROUTER_BASE_URL", "not-a-url")]));
    _ = Assert.IsType<InvalidOperationException>(ex);
  }

  [Fact]
  public void Invalid_Zai_Base_Url_Throws_InvalidOperationException()
  {
    Exception ex = Record.Exception(() => Load(env: [
        ("ZAI_API_KEY", "k"), ("ZAI_BASE_URL", "not-a-url")]));
    InvalidOperationException invalid = Assert.IsType<InvalidOperationException>(ex);
    Assert.Contains("ZAI_BASE_URL", invalid.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Invalid_SubAgent_Configuration_Throws()
  {
    Exception ex = Record.Exception(() => Load(env: [
        ("OPENROUTER_API_KEY", "k"), ("SubAgent__MaxConcurrentAgents", "0")]));
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
