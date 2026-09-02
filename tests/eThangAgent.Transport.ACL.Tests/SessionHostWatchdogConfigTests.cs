using eThangAgent.Agent.Application;
using eThangAgent.ChildHost;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>W1.2 host-side consumption: SessionHost translates the SubAgent:Watchdog
///     section shipped inside the settings JSON into the options the host watchdog
///     runs under. Configured values govern EXACTLY (no clamping, no substitution);
///     a settings JSON without the section leaves every knob at WatchdogOptions.Default.
///     The values' trip: app config -> AgentSettings.Watchdog -> settings JSON ->
///     SessionHost.EffectiveWatchdogOptions -> ChildHostServer.BuildChildWatchdog.</summary>
public class SessionHostWatchdogConfigTests
{
  private static string WriteSettings(string json)
  {
    string path = Path.Combine(Path.GetTempPath(), "ethang-w1x-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, json);
    return path;
  }

  [Fact]
  public void SettingsJson_WithWatchdogSection_GovernsTheHostWatchdogExactly()
  {
    string path = WriteSettings(/*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-test\",\"BaseUrl\":\"http://openrouter.test\"},\"Zai\":{\"ApiKey\":null,\"BaseUrl\":\"http://zai.test\"},\"SubAgents\":{\"MaxConcurrentAgents\":1},\"Watchdog\":{\"IdleThreshold\":\"00:00:02\",\"TickInterval\":\"00:00:01\",\"MaxWrapUpAttempts\":2}}");
    try
    {
      SessionHost host = SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-w1x-" + Guid.NewGuid().ToString("N") + ".db"));
      WatchdogOptions options = host.EffectiveWatchdogOptions;
      Assert.Equal(TimeSpan.FromSeconds(2), options.IdleThreshold);
      Assert.Equal(TimeSpan.FromSeconds(1), options.TickInterval);
      Assert.Equal(2, options.MaxWrapUpAttempts);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void SettingsJson_WithoutWatchdogSection_KeepsDefaults()
  {
    string path = WriteSettings(/*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-test\",\"BaseUrl\":\"http://openrouter.test\"},\"Zai\":{\"ApiKey\":null,\"BaseUrl\":\"http://zai.test\"},\"SubAgents\":{\"MaxConcurrentAgents\":1}}");
    try
    {
      SessionHost host = SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-w1x-" + Guid.NewGuid().ToString("N") + ".db"));
      Assert.Equal(WatchdogOptions.Default.IdleThreshold, host.EffectiveWatchdogOptions.IdleThreshold);
      Assert.Equal(WatchdogOptions.Default.TickInterval, host.EffectiveWatchdogOptions.TickInterval);
      Assert.Equal(WatchdogOptions.Default.MaxWrapUpAttempts, host.EffectiveWatchdogOptions.MaxWrapUpAttempts);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void SettingsJson_MissingRequiredMember_IsANamedStartupError_NotAnNRE()
  {
    string path = WriteSettings(/*lang=json,strict*/ "{\"SubAgents\":{\"MaxConcurrentAgents\":1}}");
    try
    {
      InvalidOperationException error = Assert.Throws<InvalidOperationException>(
          () => SessionHost.Create(path, Path.Combine(Path.GetTempPath(), "ethang-w1x-" + Guid.NewGuid().ToString("N") + ".db")));
      Assert.Contains("missing required member(s)", error.Message, StringComparison.Ordinal);
      Assert.Contains("OpenRouter", error.Message, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(path);
    }
  }
}
