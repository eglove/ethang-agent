using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Full-chain integration (spec #55): usage observation -> EWMA -> crossing
/// detection -> turn-start re-pick -> pin rewrite + notice + audit, driven through the
/// REAL <see cref="SendMessageCommandHandler"/> with a wired
/// <see cref="DynamicReselectionCoordinator"/>, the real <see cref="MeasuredMixPolicy"/>
/// and <see cref="SmartModelSelectorCore"/>, and a fake catalog/store/provider.</summary>
public class MeasuredMixChainTests
{
  private const string FallbackModel = "openrouter/auto";

  // The Task 3/6 price pair: A is cheap-input / expensive-output, B is
  // expensive-input / cheap-output. Break-even at w = 0.0667 output tokens per input
  // token - A wins below, B above. Both entries are model id "m/x" (the Task 3/6 P()
  // helper shape), so the notice names the PROVIDER ("via B").
  private static ModelProviderEntry P(string prov, decimal inM, decimal outM) =>
      new("m/x", prov, inM / 1_000_000m, outM / 1_000_000m, 128000, 0, true, false, 50, 80, null, null, null, null);

  private sealed class FixedCatalog(params ModelProviderEntry[] entries) : IModelCatalog
  {
    public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(entries));
  }

  /// <summary>A provider that reports a fixed per-turn token usage on every call.</summary>
  private sealed class UsageProvider(int inputTokens, int outputTokens) : IModelProvider
  {
    public string? LastModelId { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastModelId = config.ModelId;
      return Task.FromResult(Result.Success(
          new ModelResponse("ok", [], Usage: new TokenUsage(inputTokens, outputTokens))));
    }
  }

  private sealed record Chain(
      FakeAgentStore Store, AgentId RootId, MeasuredMixPolicy Policy,
      SendMessageCommandHandler Handler, UsageProvider Provider, List<string> Notices);

  private static async Task<Chain> BuildAsync(decimal? seed, int inputTokens, int outputTokens, string? modelUsed)
  {
    FakeAgentStore store = new();
    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(
        AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "C:/workspaces/demo", "openrouter", modelUsed),
        TestContext.Current.CancellationToken).ConfigureAwait(false);
    RootSessionIdentity identity = new() { Id = rootId };
    FixedCatalog catalog = new(P("A", 0.04m, 0.50m), P("B", 0.07m, 0.05m));
    MeasuredMixPolicy policy = new(seed);
    DynamicReselectionCoordinator coordinator = new(
        catalog, static () => SmartSelectionOptions.Default, preferences: null,
        policy, store, identity, FallbackModel);
    Conversation conversation = new();
    UsageProvider provider = new(inputTokens, outputTokens);
    RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]),
        onUsage: usage => policy.Observe(new UsageObservation(usage.InputTokens, usage.OutputTokens)));
    RootModelContext context = new(
        store, identity, FallbackModel, 2048, 0.7f, new FixedWindowSource(), catalog);
    RootAgentResolver resolver = new(context, new SmartModelSelector(catalog));
    SendMessageCommandHandler handler = new(
        conversation: conversation, rootHolder: holder, rootResolver: resolver, reselection: coordinator);
    return new Chain(store, rootId, policy, handler, provider, []);
  }

  private static async Task SendTurnAsync(Chain chain, string text)
  {
    Result<string> result = await chain.Handler.Handle(
        new SendMessageCommand(text), onNotice: chain.Notices.Add, ct: TestContext.Current.CancellationToken)
        .ConfigureAwait(false);
    Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
  }

  private static async Task<IReadOnlyList<Message>> TranscriptAsync(Chain chain)
  {
    Result<IReadOnlyList<Message>> transcript = await chain.Store.GetTranscriptAsync(
        chain.RootId, TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(transcript.IsSuccess);
    return transcript.Value;
  }

  [Fact]
  public async Task Chain_Crossing_RepicksRewritesPinNotifiesAndAudits()
  {
    // Seed LOW (0.05): A is the incumbent baseline at the seed weight. The session starts
    // pinned to a DIFFERENT model, so the crossing's pin rewrite is observable as a real
    // change ("old/model" -> "m/x").
    Chain chain = await BuildAsync(seed: 0.05m, inputTokens: 100, outputTokens: 400, modelUsed: "old/model");

    // Turn 1: the policy is untrusted (0 observations) - the coordinator is silent and the
    // pinned pair serves. No notice, no audit.
    await SendTurnAsync(chain, "turn 1");
    Assert.False(chain.Policy.IsTrusted);
    Assert.Empty(chain.Notices);
    Assert.Equal("old/model", chain.Provider.LastModelId);
    Assert.Empty(await TranscriptAsync(chain));

    // Turns 2-5: output-heavy usage accumulates; the trust gate (5 turns) closes after turn 5.
    for (int turn = 2; turn <= 5; turn++)
    {
      await SendTurnAsync(chain, $"turn {turn}");
    }

    Assert.True(chain.Policy.IsTrusted);
    Assert.Equal(5, chain.Policy.Totals.Turns);
    Assert.Empty(await TranscriptAsync(chain)); // still no audit: no crossing has fired yet

    // Turn 6: the safe point runs with a trusted policy whose measured weight has crossed
    // the break-even -> re-pick B, pin rewrite, notice, audit.
    await SendTurnAsync(chain, "turn 6");

    string notice = Assert.Single(chain.Notices);
    Assert.Contains("Model selected: m/x via B", notice, StringComparison.Ordinal);
    Result<AgentRecord> record = await chain.Store.GetAsync(chain.RootId, TestContext.Current.CancellationToken);
    Assert.True(record.IsSuccess);
    Assert.Equal("m/x", record.Value.ModelUsed);
    Message audit = Assert.Single(await TranscriptAsync(chain));
    Assert.Equal(Role.System, audit.Role);
    Assert.Contains("[reselection]", audit.Content, StringComparison.Ordinal);
    Assert.Contains("old/model -> m/x via B", audit.Content, StringComparison.Ordinal);
    Assert.Equal("m/x", chain.Provider.LastModelId); // the rewritten pin serves this same turn

    // Turn 7: the pin serves the re-picked pair on subsequent turns; no further notice or audit.
    await SendTurnAsync(chain, "turn 7");
    Assert.Equal("m/x", chain.Provider.LastModelId);
    _ = Assert.Single(chain.Notices);
    _ = Assert.Single(await TranscriptAsync(chain));
  }

  [Fact]
  public async Task Chain_SubMarginCrossing_ProducesNoNotice()
  {
    // Seed LOW (0.05) so A is the incumbent; drive usage whose weight crosses the
    // break-even only by a SUB-MARGIN delta -> the policy absorbs it silently: no notice,
    // no audit row, pin unchanged (churn-free).
    Chain chain = await BuildAsync(seed: 0.05m, inputTokens: 1000, outputTokens: 80, modelUsed: "old/model");

    for (int turn = 1; turn <= 6; turn++)
    {
      await SendTurnAsync(chain, $"turn {turn}");
    }

    Assert.True(chain.Policy.IsTrusted);
    Assert.Empty(chain.Notices);
    Assert.Empty(await TranscriptAsync(chain));
    Result<AgentRecord> record = await chain.Store.GetAsync(chain.RootId, TestContext.Current.CancellationToken);
    Assert.True(record.IsSuccess);
    Assert.Equal("old/model", record.Value.ModelUsed);
  }
}
