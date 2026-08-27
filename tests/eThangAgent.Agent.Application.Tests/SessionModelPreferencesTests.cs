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
}
