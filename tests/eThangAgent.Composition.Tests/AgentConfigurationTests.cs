namespace eThangAgent.Composition.Tests;

public class AgentConfigurationTests
{
  [Fact]
  public void Missing_Api_Key_Is_Null_Not_Throw() =>
      Assert.Null(Load(env: []).ApiKey);

  [Fact]
  public void Api_Key_Is_Read_From_Environment()
  {
    AgentSettings s = Load(env: [("OPENROUTER_API_KEY", "sk-or-test")]);
    Assert.Equal("sk-or-test", s.ApiKey);
  }

  [Fact]
  public void Base_Url_Defaults_To_OpenRouter()
  {
    AgentSettings s = Load(env: []);
    Assert.Equal(new Uri("https://openrouter.ai"), s.BaseUrl);
  }

  [Fact]
  public void Base_Url_Override_Is_Honored()
  {
    const string loopback = "http://localhost:5599"; // devskim: ignore DS162092 - loopback override is the behavior under test
    AgentSettings s = Load(env: [("OPENROUTER_BASE_URL", loopback)]);
    Assert.Equal(new Uri(loopback), s.BaseUrl);
  }

  [Fact]
  public void Invalid_Base_Url_Throws_InvalidOperationException()
  {
    Exception ex = Record.Exception(() => Load(env: [
        ("OPENROUTER_API_KEY", "k"), ("OPENROUTER_BASE_URL", "not-a-url")]));
    _ = Assert.IsType<InvalidOperationException>(ex);
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
