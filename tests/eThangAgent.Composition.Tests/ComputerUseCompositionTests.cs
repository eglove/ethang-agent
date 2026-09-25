using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ComputerUse.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Task 19: computer-use composition gating. The loader binds computer_use_enabled
///     strictly (absent=false; true/false; junk = startup error naming the key). The container gates
///     on settings.ComputerUse: disabled yields no 'computer' action anywhere (agent tools, ExecGuide
///     render, ChildToolSurface); enabled yields the tool in all three with a working access seam.</summary>
public class ComputerUseCompositionTests
{
  private static AgentSettings Settings(bool computerUse) => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2),
      ComputerUse: computerUse);

  private static ServiceProvider Build(bool computerUse, string providerName = Providers.OpenRouter)
  {
    return new ServiceCollection()
        .AddEThangAgentCore(Settings(computerUse), providerName,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("ws"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }


  // ---- loader parse matrix (strict binding, ParseRemoteHost pattern) ----

  [Fact]
  public async Task LoadAsync_ComputerUse_Absent_BindsFalse()
  {
    AgentSettings settings = await AgentSettingsLoader.LoadAsync(new FakePreferenceStore());
    Assert.False(settings.ComputerUse);
  }

  [Fact]
  public async Task LoadAsync_ComputerUse_True_BindsTrue()
  {
    FakePreferenceStore prefs = new FakePreferenceStore()
        .With(AgentPreferenceKeys.ComputerUseEnabled, "true");
    Assert.True((await AgentSettingsLoader.LoadAsync(prefs)).ComputerUse);
  }

  [Fact]
  public async Task LoadAsync_ComputerUse_False_BindsFalse()
  {
    FakePreferenceStore prefs = new FakePreferenceStore()
        .With(AgentPreferenceKeys.ComputerUseEnabled, "false");
    Assert.False((await AgentSettingsLoader.LoadAsync(prefs)).ComputerUse);
  }

  [Theory]
  [InlineData("yes")]
  [InlineData("1")]
  [InlineData("on")]
  [InlineData(" ")]
  public async Task LoadAsync_ComputerUse_Junk_IsStartupErrorNamingTheKey(string stored)
  {
    FakePreferenceStore prefs = new FakePreferenceStore()
        .With(AgentPreferenceKeys.ComputerUseEnabled, stored);
    InvalidOperationException invalid = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
    Assert.Contains(AgentPreferenceKeys.ComputerUseEnabled, invalid.Message, StringComparison.Ordinal);
  }

  // ---- gating matrix ----

  [Fact]
  public void Disabled_Computer_NotInAgentTools()
  {
    using ServiceProvider services = Build(computerUse: false);
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.DoesNotContain(tools.Actions, a => a.Name == "computer");
  }

  [Fact]
  public void Disabled_Computer_NotInExecGuide()
  {
    using ServiceProvider services = Build(computerUse: false);
    string guide = services.GetRequiredService<ISystemPromptProvider>().Build();
    Assert.DoesNotContain("computer", guide, StringComparison.Ordinal);
  }

  [Fact]
  public void Disabled_Computer_NotInChildToolSurface()
  {
    using ServiceProvider services = Build(computerUse: false);
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.False(surface().Resolve("computer").IsSuccess);
  }

  [Fact]
  public void Enabled_Computer_InAgentTools()
  {
    using ServiceProvider services = Build(computerUse: true);
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "computer");
  }

  [Fact]
  public void Enabled_Computer_InExecGuide()
  {
    using ServiceProvider services = Build(computerUse: true);
    string guide = services.GetRequiredService<ISystemPromptProvider>().Build();
    Assert.Contains("computer", guide, StringComparison.Ordinal);
  }

  [Fact]
  public void Enabled_Computer_InChildToolSurface()
  {
    using ServiceProvider services = Build(computerUse: true);
    Func<ICapabilityRegistry> surface = services.GetRequiredService<Func<ICapabilityRegistry>>();
    Assert.True(surface().Resolve("computer").IsSuccess);
  }

  [Fact]
  public void Settings_ComputerUse_PersistedInJson()
  {
    // The flag travels in the settings JSON to remote hosts like every other setting.
    System.Text.Json.JsonSerializerOptions options = new(System.Text.Json.JsonSerializerDefaults.Web);
    AgentSettings settings = Settings(computerUse: true);
    string json = System.Text.Json.JsonSerializer.Serialize(settings, options);
    Assert.Contains("\"computerUse\":true", json, StringComparison.Ordinal);
    AgentSettings parsed = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(json, options)!;
    Assert.True(parsed.ComputerUse);
  }

  [Fact]
  public void Enabled_ResolvedComputerAccess_IsBrokerBacked()
  {
    using ServiceProvider services = Build(computerUse: true);
    IComputerAccessProvider provider = services.GetRequiredService<IComputerAccessProvider>();
    IComputerAccess access = provider.ForWorkspace("ws")
        ?? throw new InvalidOperationException("broker-backed access missing");
    _ = Assert.IsType<BrokerComputerAccess>(access);
  }
}
