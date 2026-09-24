
namespace eThangAgent.Composition.Tests;

/// <summary>verification_gate_enabled: absent = enabled (default-on decision);
///     only exact 'true'/'false' bind; anything else is a startup error.</summary>
public class VerificationGateSettingsTests
{
  [Fact]
  public async Task Absent_DefaultsToEnabled()
  {
    AgentSettings s = await AgentSettingsLoader.LoadAsync(new FakePreferenceStore());
    Assert.True(s.VerificationGateEnabled);
  }

  [Fact]
  public async Task ExplicitFalse_BindsFalse()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.VerificationGateEnabled, "false");
    AgentSettings s = await AgentSettingsLoader.LoadAsync(prefs);
    Assert.False(s.VerificationGateEnabled);
  }

  [Fact]
  public async Task ExplicitTrue_BindsTrue()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.VerificationGateEnabled, "true");
    AgentSettings s = await AgentSettingsLoader.LoadAsync(prefs);
    Assert.True(s.VerificationGateEnabled);
  }

  [Fact]
  public async Task CapitalizedTrue_IsStartupError()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.VerificationGateEnabled, "True");
    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
  }

  [Fact]
  public async Task NumericOne_IsStartupError()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.VerificationGateEnabled, "1");
    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
  }
}
