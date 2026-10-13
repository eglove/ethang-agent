using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application.Tests;

public class SessionModelPreferencesTests
{
  /// <summary>Session context for the resolver tests; only the preferred-model test
  ///     wires persistence.</summary>
  private static RootModelContext Ctx(FakeAgentStore? store, AgentId? rootId, string fallbackModel) =>
      new(store, rootId is null ? null : new RootSessionIdentity() { Id = rootId.Value },
          fallbackModel, 2048, 0.7f, new FixedWindowSource());

  [Fact]
  public async Task RootResolver_NoPreference_LeavesEffortUnset()
  {
    RootAgentResolver resolver = new(Ctx(null, null, "openrouter/auto"));

    (ModelConfig config, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Null(config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferenceChange_BetweenTurns_IsPickedUp()
  {
    SessionModelPreferences preferences = new();
    RootAgentResolver resolver = new(Ctx(null, null, "openrouter/auto"), preferences: preferences);

    (ModelConfig first, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ReasoningEffort = ReasoningEffort.None;
    (ModelConfig second, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Null(first.Effort);
    Assert.Equal(ReasoningEffort.None, second.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_CarriesPreferredEffort()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3", ReasoningEffort = ReasoningEffort.High };
    RootAgentResolver resolver = new(Ctx(null, null, "glm-5.3-flash"), preferences: preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("glm-5.3", config.ModelId);
    Assert.Equal(ReasoningEffort.High, config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_AnnouncedOnce_ThenSilent()
  {
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter"), ct: TestContext.Current.CancellationToken);
    SessionModelPreferences preferences = new();
    RootAgentResolver resolver = new(Ctx(store, rootId, "glm-5.3-flash"), preferences: preferences);

    (ModelConfig first, string? firstNotice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ModelId = "glm-5.3";
    (ModelConfig second, string? secondNotice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    (ModelConfig third, string? thirdNotice) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("glm-5.3-flash", first.ModelId);
    Assert.Null(firstNotice);
    Assert.Equal("glm-5.3", second.ModelId);
    Assert.Equal("Model selected: glm-5.3", secondNotice);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("glm-5.3", updated.ModelUsed);
    Assert.Equal("glm-5.3", third.ModelId);
    Assert.Null(thirdNotice);
  }

  [Fact]
  public async Task RootResolver_ModelChoiceMutated_BetweenTurns_IsPickedUp()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3-flash" };
    RootAgentResolver resolver = new(Ctx(null, null, "glm-5.3-flash"), preferences: preferences);

    (ModelConfig first, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ModelId = "glm-5.3";
    (ModelConfig second, _) = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.Equal("glm-5.3-flash", first.ModelId);
    Assert.Equal("glm-5.3", second.ModelId);
  }
}
