namespace eThangAgent.Composition.Tests;

/// <summary>skill_registry:default_target: absent = unset (installs demand an
///     explicit target); only exact 'global'/'workspace' bind; anything else is
///     a startup error naming the key.</summary>
public class SkillRegistrySettingsTests
{
  [Fact]
  public async Task Absent_DefaultsToUnset()
  {
    AgentSettings s = await AgentSettingsLoader.LoadAsync(new FakePreferenceStore());
    Assert.Null(s.SkillRegistryDefaultTarget);
  }

  [Fact]
  public async Task ExplicitGlobal_BindsGlobal()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.SkillRegistryDefaultTarget, "global");
    AgentSettings s = await AgentSettingsLoader.LoadAsync(prefs);
    Assert.Equal("global", s.SkillRegistryDefaultTarget);
  }

  [Fact]
  public async Task ExplicitWorkspace_BindsWorkspace()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.SkillRegistryDefaultTarget, "workspace");
    AgentSettings s = await AgentSettingsLoader.LoadAsync(prefs);
    Assert.Equal("workspace", s.SkillRegistryDefaultTarget);
  }

  [Fact]
  public async Task CapitalizedGlobal_IsStartupError()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.SkillRegistryDefaultTarget, " GLOBAL ");
    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
  }

  [Fact]
  public async Task Both_IsStartupError()
  {
    FakePreferenceStore prefs = new FakePreferenceStore().With(AgentPreferenceKeys.SkillRegistryDefaultTarget, "both");
    _ = await Assert.ThrowsAsync<InvalidOperationException>(
        () => AgentSettingsLoader.LoadAsync(prefs));
  }
}