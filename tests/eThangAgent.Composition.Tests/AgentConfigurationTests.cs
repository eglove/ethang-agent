using eThangAgent.Composition;

namespace eThangAgent.Composition.Tests;

public class AgentConfigurationTests
{
    [Fact]
    public void Missing_Api_Key_Is_Null_Not_Throw() =>
        Assert.Null(Load(env: []).ApiKey);

    [Fact]
    public void Api_Key_Is_Read_From_Environment()
    {
        var s = Load(env: [("OPENROUTER_API_KEY", "sk-or-test")]);
        Assert.Equal("sk-or-test", s.ApiKey);
    }

    [Fact]
    public void Base_Url_Defaults_To_OpenRouter()
    {
        var s = Load(env: []);
        Assert.Equal(new Uri("https://openrouter.ai"), s.BaseUrl);
    }

    [Fact]
    public void Base_Url_Override_Is_Honored()
    {
        var s = Load(env: [("OPENROUTER_BASE_URL", "http://localhost:5599")]);
        Assert.Equal(new Uri("http://localhost:5599"), s.BaseUrl);
    }

    [Fact]
    public void Invalid_Base_Url_Throws_InvalidOperationException()
    {
        var ex = Record.Exception(() => Load(env: [
            ("OPENROUTER_API_KEY", "k"), ("OPENROUTER_BASE_URL", "not-a-url")]));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Invalid_SubAgent_Configuration_Throws()
    {
        var ex = Record.Exception(() => Load(env: [
            ("OPENROUTER_API_KEY", "k"), ("SubAgent__MaxConcurrentAgents", "0")]));
        Assert.IsType<InvalidOperationException>(ex);
    }

    // Agent:MaxToolIterations was removed with the iteration cap: an unknown key is
    // simply ignored by configuration, never a startup error.

    private static AgentSettings Load(params (string Key, string Value)[] env)
    {
        foreach (var (key, value) in env) Environment.SetEnvironmentVariable(key, value);
        try { return AgentConfiguration.Load(); }
        finally { foreach (var (key, _) in env) Environment.SetEnvironmentVariable(key, null); }
    }
}
