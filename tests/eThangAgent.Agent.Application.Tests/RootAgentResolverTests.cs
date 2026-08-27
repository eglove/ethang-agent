using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

public class RootAgentResolverTests
{
  private sealed class FakeModelSelector(ModelSelectionResult result) : IModelSelector
  {
    private readonly ModelSelectionResult _result = result;
    public int Calls { get; private set; }
    public Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(_result));
    }
  }

  private sealed class FailingModelSelector : IModelSelector
  {
    public Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, IReadOnlySet<string>? excludedKeys = null, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<ModelSelectionResult>(new DomainError("SelectionFailed", "boom")));
  }

  private static ModelSelectionResult Selection(string modelId, string providerName = "TestProvider") => new(modelId, providerName,
      new TaskCategory(["coding"], 3, false, false, null, null),
      new ModelFilter(null, null, null, null, null, null, null, null, null, null, null), "reason");

  private static async Task<AgentId> SeedRootAsync(FakeAgentStore store)
  {
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    return rootId;
  }

  private static RootSessionIdentity Identity(AgentId rootId) => new() { Id = rootId };

  [Fact]
  public async Task ExplicitModel_Wins_And_Never_Runs_Selection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    ModelConfig explicitModel = ModelConfig.Create("pinned/model", null, 1024, 0.5f).Value!;
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel, 2048, 0.7f);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(new Conversation(), "any task");

    Assert.Equal("pinned/model", config.ModelId);
    Assert.Null(notice);
    Assert.Equal(0, selector.Calls);
  }

  [Fact]
  public async Task NoSelector_Uses_Fallback_Without_Selection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    RootAgentResolver resolver = new(selector: null, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(new Conversation(), "any task");

    Assert.Equal(RootAgentResolver.FallbackModel, config.ModelId);
    Assert.Null(notice);
  }

  [Fact]
  public async Task FirstTurn_ZeroPriorUserMessages_RunsSelection_AndPersistsModel()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(new Conversation(), "write a C# function");

    Assert.Equal("anthropic/claude-3.5-sonnet", config.ModelId);
    Assert.NotNull(notice);
    Assert.Contains("Model selected: anthropic/claude-3.5-sonnet", notice, StringComparison.Ordinal);
    Assert.Equal(1, selector.Calls);
    AgentRecord updated = Assert.Single(store.Updated);
    Assert.Equal("anthropic/claude-3.5-sonnet", updated.ModelUsed);
  }

  [Fact]
  public async Task SelectionFailure_FallsBack_And_SurfacesNotice()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FailingModelSelector selector = new();
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal(RootAgentResolver.FallbackModel, config.ModelId);
    Assert.NotNull(notice);
    Assert.Contains("Model selection failed: boom", notice, StringComparison.Ordinal);
    Assert.Contains($"using {RootAgentResolver.FallbackModel}", notice, StringComparison.Ordinal);
    Assert.Empty(store.Updated);
  }

  [Fact]
  public async Task OffCadence_Turn_DoesNotRunSelection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    // Seed one prior user message (turn 1 already happened): turn 2 is off-cadence.
    Conversation conversation = new();
    conversation.AddUserMessage("first turn");

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(conversation, "second turn");

    Assert.Equal(RootAgentResolver.FallbackModel, config.ModelId);
    Assert.Null(notice);
    Assert.Equal(0, selector.Calls);
  }

  [Fact]
  public async Task CadenceBoundary_AtTenPriorUserMessages_RunsSelection()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("google/gemini-2.0-flash-001"));
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    // Ten prior user messages: 10 % 10 == 0 → cadence boundary (the 11th turn reclassifies).
    Conversation conversation = new();
    for (int i = 0; i < 10; i++)
    {
      conversation.AddUserMessage($"turn {i}");
    }

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(conversation, "eleventh turn");

    Assert.Equal("google/gemini-2.0-flash-001", config.ModelId);
    Assert.NotNull(notice);
    Assert.Equal(1, selector.Calls);
  }

  [Fact]
  public async Task Selection_WithUnchangedModel_DoesNotEmitNotice()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    // Pre-persist the model so persistence is a no-op (ModelUsed already matches).
    _ = await store.UpdateAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow) with { ModelUsed = "anthropic/claude-3.5-sonnet" }).ConfigureAwait(true);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet"));
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);

    (ModelConfig config, string? notice) = await resolver.ResolveAsync(new Conversation(), "task");

    Assert.Equal("anthropic/claude-3.5-sonnet", config.ModelId);
    Assert.Null(notice); // no change → no notice
  }

  [Fact]
  public async Task ResolveAsync_NullConversation_Throws()
  {
    RootAgentResolver resolver = new(selector: null, store: null, identity: null, explicitModel: null, 2048, 0.7f);
    _ = await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, "task"));
  }
  [Fact]
  public async Task FirstTurn_SelectionCarriesProviderIntoConfig()
  {
    FakeAgentStore store = new();
    AgentId rootId = await SeedRootAsync(store);
    FakeModelSelector selector = new(Selection("anthropic/claude-3.5-sonnet", "Anthropic"));
    RootAgentResolver resolver = new(selector, store, Identity(rootId), explicitModel: null, 2048, 0.7f);
    (ModelConfig config, _) = await resolver.ResolveAsync(new Conversation(), "write a C# function");

    Assert.Equal("anthropic/claude-3.5-sonnet", config.ModelId);
    Assert.Equal("Anthropic", config.Provider);
  }
}
