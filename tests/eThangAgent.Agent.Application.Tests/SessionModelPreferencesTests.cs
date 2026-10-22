using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class SessionModelPreferencesTests
{
  /// <summary>Session context for the resolver tests; only the preferred-model test
  ///     wires persistence.</summary>
  private static RootModelContext Ctx(FakeAgentStore? store, AgentId? rootId, string fallbackModel) =>
      new(store, rootId is null ? null : new RootSessionIdentity() { Id = rootId.Value },
          fallbackModel, 2048, 0.7f, new FixedWindowSource());

  [Fact]
  public async Task RootResolver_NoEffortPreference_LeavesEffortUnset()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    RootAgentResolver resolver = new(Ctx(null, null, "openrouter/auto"), preferences: preferences);

    Result<(ModelConfig Config, string? Notice)> resolution = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Null(resolution.Value.Config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferenceChange_BetweenTurns_IsPickedUp()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    RootAgentResolver resolver = new(Ctx(null, null, "openrouter/auto"), preferences: preferences);

    Result<(ModelConfig Config, string? Notice)> first = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ReasoningEffort = ReasoningEffort.None;
    Result<(ModelConfig Config, string? Notice)> second = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Null(first.Value.Config.Effort);
    Assert.Equal(ReasoningEffort.None, second.Value.Config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_CarriesPreferredEffort()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3", ReasoningEffort = ReasoningEffort.High };
    RootAgentResolver resolver = new(Ctx(null, null, "glm-5.3-flash"), preferences: preferences);

    Result<(ModelConfig Config, string? Notice)> resolution = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(resolution.IsSuccess, resolution.Error?.Message);
    Assert.Equal("glm-5.3", resolution.Value.Config.ModelId);
    Assert.Equal(ReasoningEffort.High, resolution.Value.Config.Effort);
  }

  [Fact]
  public async Task RootResolver_PreferredModel_AnnouncedOnce_ThenSilent()
  {
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter", "glm-5.3-flash"), ct: TestContext.Current.CancellationToken);
    SessionModelPreferences preferences = new();
    RootAgentResolver resolver = new(Ctx(store, rootId, "openrouter/auto"), preferences: preferences);

    Result<(ModelConfig Config, string? Notice)> first = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ModelId = "glm-5.3";
    Result<(ModelConfig Config, string? Notice)> second = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    Result<(ModelConfig Config, string? Notice)> third = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.True(third.IsSuccess, third.Error?.Message);
    Assert.Equal("glm-5.3-flash", first.Value.Config.ModelId);
    Assert.Null(first.Value.Notice);
    Assert.Equal("glm-5.3", second.Value.Config.ModelId);
    Assert.Equal("Model selected: glm-5.3", second.Value.Notice);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("glm-5.3", updated.ModelUsed);
    Assert.Equal("glm-5.3", third.Value.Config.ModelId);
    Assert.Null(third.Value.Notice);
  }

  [Fact]
  public async Task RootResolver_ModelChoiceMutated_BetweenTurns_IsPickedUp()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3-flash" };
    RootAgentResolver resolver = new(Ctx(null, null, "glm-5.3-flash"), preferences: preferences);

    Result<(ModelConfig Config, string? Notice)> first = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);
    preferences.ModelId = "glm-5.3";
    Result<(ModelConfig Config, string? Notice)> second = await resolver.ResolveAsync(ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal("glm-5.3-flash", first.Value.Config.ModelId);
    Assert.Equal("glm-5.3", second.Value.Config.ModelId);
  }

  [Fact]
  public void CopyFrom_AdoptsEveryKnob()
  {
    // The host applies the app's carried preferences onto its OWN singleton, so consumers
    // holding that instance must see the travelled values.
    SessionModelPreferences target = new();
    // The opaque provider-settings carrier: a JSON document the preferences never
    // interpret, so it is written as a raw literal rather than an escaped one.
    // JSON002 fires only in the format/IDE host; the pragma pair is the repo's named
    // decision for hand-written JSON test shapes.
#pragma warning disable JSON002 // Probable JSON string detected
    string providerSettings =
        """
        {"a":1}
        """;
#pragma warning restore JSON002 // Probable JSON string detected
    SessionModelPreferences source = new()
    {
      ModelId = "minimax/minimax-m3.1-flash:free",
      ReasoningEffort = ReasoningEffort.Max,
      Temperature = 0.3f,
      MaxTokens = 8192,
      TopP = 0.9f,
      TopK = 40,
      FrequencyPenalty = 0.1f,
      PresencePenalty = 0.2f,
      RepetitionPenalty = 1.05f,
      MinP = 0.05f,
      TopA = 0.5f,
      Seed = 7,
      Verbosity = VerbosityLevel.High,
      ParallelToolCalls = false,
      ProviderSettings = providerSettings,
    };

    target.CopyFrom(source);

    Assert.Equal("minimax/minimax-m3.1-flash:free", target.ModelId);
    Assert.Equal(ReasoningEffort.Max, target.ReasoningEffort);
    Assert.Equal(0.3f, target.Temperature);
    Assert.Equal(8192, target.MaxTokens);
    Assert.Equal(0.9f, target.TopP);
    Assert.Equal(40, target.TopK);
    Assert.Equal(0.1f, target.FrequencyPenalty);
    Assert.Equal(0.2f, target.PresencePenalty);
    Assert.Equal(1.05f, target.RepetitionPenalty);
    Assert.Equal(0.05f, target.MinP);
    Assert.Equal(0.5f, target.TopA);
    Assert.Equal(7, target.Seed);
    Assert.Equal(VerbosityLevel.High, target.Verbosity);
    Assert.False(target.ParallelToolCalls);
    Assert.Equal(providerSettings, target.ProviderSettings);
  }
}
