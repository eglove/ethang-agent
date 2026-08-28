using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class SessionModelPreferencesTests
{
  private static ModelSelectionResult Selection(string modelId, string providerName = "TestProvider") => new(
      modelId, providerName,
      new TaskCategory(["coding"], 3, false, false, null, null),
      new ModelFilter(null, null, null, null, null, null, null, null, null, null, null), "reason");

  private sealed class FakeModelSelector(ModelSelectionResult result) : IModelSelector
  {
    public Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success(result));
  }

  [Fact]
  public async Task RootResolver_SelectionPath_AppliesPreferredEffort()
  {
    SessionModelPreferences preferences = new() { ReasoningEffort = ReasoningEffort.Low };
    RootAgentResolver resolver = new(
        new FakeModelSelector(Selection("anthropic/claude-3.5-sonnet")), store: null, identity: null,
        explicitModel: null, "openrouter/auto", 2048, 0.7f, preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal(ReasoningEffort.Low, config.Effort);
  }

  [Fact]
  public async Task RootResolver_ExplicitPin_StillCarriesPreferredEffort()
  {
    SessionModelPreferences preferences = new() { ReasoningEffort = ReasoningEffort.Max };
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null,
        explicitModel: ModelConfig.Create("glm-5.3", null, 1024, 0.5f).Value!,
        "glm-5.3-flash", 2048, 0.7f, preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal("glm-5.3", config.ModelId);
    Assert.Equal(ReasoningEffort.Max, config.Effort);
  }

  [Fact]
  public async Task RootResolver_NoPreference_LeavesEffortUnset()
  {
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null, explicitModel: null,
        "openrouter/auto", 2048, 0.7f);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Null(config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferenceChange_BetweenTurns_IsPickedUp()
  {
    SessionModelPreferences preferences = new();
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null, explicitModel: null,
        "openrouter/auto", 2048, 0.7f, preferences);
    Conversation conversation = new();

    (ModelConfig first, _) = await resolver.ResolveAsync(conversation, "turn one");
    preferences.ReasoningEffort = ReasoningEffort.None;
    (ModelConfig second, _) = await resolver.ResolveAsync(conversation, "turn two");

    Assert.Null(first.Effort);
    Assert.Equal(ReasoningEffort.None, second.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_BeatsSelection()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    RootAgentResolver resolver = new(
        new FakeModelSelector(Selection("anthropic/claude-3.5-sonnet")), store: null, identity: null,
        explicitModel: null, "glm-5.3-flash", 2048, 0.7f, preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal("glm-5.3", config.ModelId);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_BeatsConfiguredPin()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3-flash" };
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null,
        explicitModel: ModelConfig.Create("glm-5.3", null, 1024, 0.5f).Value!,
        "glm-5.3-flash", 2048, 0.7f, preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal("glm-5.3-flash", config.ModelId);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_CarriesPreferredEffort()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3", ReasoningEffort = ReasoningEffort.High };
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null, explicitModel: null,
        "glm-5.3-flash", 2048, 0.7f, preferences);

    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal("glm-5.3", config.ModelId);
    Assert.Equal(ReasoningEffort.High, config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_AnnouncedOnce_ThenSilent()
  {
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
    SessionModelPreferences preferences = new();
    RootAgentResolver resolver = new(
        selector: null, store, new RootSessionIdentity() { Id = rootId }, explicitModel: null,
        "glm-5.3-flash", 2048, 0.7f, preferences);
    Conversation conversation = new();

    (ModelConfig first, string? firstNotice) = await resolver.ResolveAsync(conversation, "turn one");
    preferences.ModelId = "glm-5.3";
    (ModelConfig second, string? secondNotice) = await resolver.ResolveAsync(conversation, "turn two");
    (ModelConfig third, string? thirdNotice) = await resolver.ResolveAsync(conversation, "turn three");

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
    RootAgentResolver resolver = new(
        selector: null, store: null, identity: null, explicitModel: null,
        "glm-5.3-flash", 2048, 0.7f, preferences);
    Conversation conversation = new();

    (ModelConfig first, _) = await resolver.ResolveAsync(conversation, "turn one");
    preferences.ModelId = "glm-5.3";
    (ModelConfig second, _) = await resolver.ResolveAsync(conversation, "turn two");

    Assert.Equal("glm-5.3-flash", first.ModelId);
    Assert.Equal("glm-5.3", second.ModelId);
  }
}
