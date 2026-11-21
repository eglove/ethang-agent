using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>ONE TURN WALKS THE SESSION'S MODEL LIST: each entry is attempted at most once, in
///     order, each attempt names exactly one model resolved to the vendor that entry names, and
///     the turn fails with one message naming every model it tried when the list is spent.
///     <para>EVERY CASE DRIVES A REAL <see cref="Agent"/> over a scripted resolver, so what is
///     asserted is the loop's own behaviour rather than a helper's. The retry wait is a
///     completed task, so no case spends clock time.</para></summary>
public class ModelWalkTests
{
  private const string A = "alpha/model";
  private const string B = "beta/model";
  private const string C = "gamma/model";

  /// <summary>One scripted provider call: the code it fails with, or the answer it gives.
  ///     </summary>
  private sealed record Reply(string? FailCode, string? Answer);

  /// <summary>A provider that answers from a per-model script and records every model it was
  ///     asked for, so one attempt's model is observable.
  ///     </summary>
  private sealed class ScriptedProvider(string modelId, params Reply[] replies) : IModelProvider
  {
    private int _next;

    public string ModelId { get; } = modelId;

    public List<string> Asked { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      Asked.Add(config.ModelId);
      Reply reply = replies[Math.Min(_next++, replies.Length - 1)];
      if (reply.FailCode is { } code)
      {
        return Task.FromResult(Result.Failure<ModelResponse>(
            new DomainError(code, $"{ModelId} could not serve.")));
      }

      onContentDelta?.Invoke(reply.Answer ?? string.Empty);
      return Task.FromResult(Result.Success(new ModelResponse(reply.Answer ?? string.Empty, [])));
    }
  }

  /// <summary>A resolver over one scripted provider per vendor id, recording every resolution
  ///     so the vendor an attempt reached is observable.</summary>
  private sealed class ScriptedResolver(Dictionary<string, IModelProvider> providers)
      : IModelProviderResolver
  {
    public List<(string Vendor, string ModelId)> Resolved { get; } = [];

    public Result<IModelProvider> Resolve(string vendorId, ModelConfig config)
    {
      Resolved.Add((vendorId, config.ModelId));
      return providers.TryGetValue(vendorId, out IModelProvider? provider)
          ? Result.Success(provider)
          : Result.Failure<IModelProvider>(new DomainError("ProviderNotConfigured",
              $"no transport for '{vendorId}'."));
    }

    public Result<IModelCatalog> Catalog(string vendorId) =>
        Result.Failure<IModelCatalog>(new DomainError("ProviderNotConfigured",
            $"no catalog for '{vendorId}'."));
  }

  private static ModelConfig DefaultConfig => ModelConfig.Create(A, 100, 0.5f, 8192).Value!;

  /// <summary>An agent walking <paramref name="entries"/> over <paramref name="resolver"/>,
  ///     with a completed retry wait so no case spends clock time.</summary>
  private static Agent Build(IReadOnlyList<ModelSelectionEntry> entries, ScriptedResolver resolver,
      IModelProvider fallback, Conversation conversation)
  {
    SessionModelPreferences preferences = new() { Entries = entries };
    return new Agent(fallback, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          TurnRetryDelay = (_, _) => Task.CompletedTask,
          ModelWalk = new ModelWalkSource(preferences, ModelVendors.OpenRouter, resolver),
        });
  }

  private static ModelSelectionEntry Entry(string id, string? vendor = null) => new(id, vendor);

  /// <summary>A throttled head is dropped and the turn continues on the next entry, which
  ///     serves it: the transcript states the drop, and the list the turn walked is unchanged.
  ///     </summary>
  [Fact]
  public async Task AThrottledHead_IsDropped_AndTheNextEntryServes()
  {
    Conversation conversation = new();
    ScriptedProvider head = new(A, new Reply("RateLimited", null));
    ScriptedProvider backup = new(B, new Reply(null, "served by the backup"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
    })
    { };
    // The walk resolves every entry through the resolver, so both entries must resolve; the
    // provider each entry reaches is what the script distinguishes.
    ScriptedResolver perModel = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
      [ModelVendors.Cloudflare] = backup,
    });
    Agent agent = Build([Entry(A), Entry(B, ModelVendors.Cloudflare)], perModel, head, conversation);
    List<(string Model, string? Vendor, string Code)> drops = [];

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnModelDropped: (model, vendor, code, _) => drops.Add((model, vendor, code))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("served by the backup", result.Value);
    // One attempt per entry, in order, each naming exactly one model.
    Assert.Equal([A], head.Asked);
    Assert.Equal([B], backup.Asked);
    // The drop is stated with the model, its vendor, and the reason.
    (string model, string? vendor, string code) = Assert.Single(drops);
    Assert.Equal(A, model);
    Assert.Equal(ModelVendors.OpenRouter, vendor);
    Assert.Equal("RateLimited", code);
    Assert.Contains(conversation.Messages, m => m.Role is Role.System
        && m.Content.Contains("[model walk]", StringComparison.Ordinal)
        && m.Content.Contains(A, StringComparison.Ordinal));
    // Both vendors were resolved, each for its own entry's model.
    Assert.Equal([(ModelVendors.OpenRouter, A), (ModelVendors.Cloudflare, B)], perModel.Resolved);
    _ = resolver;
  }

  /// <summary>A rejected model is dropped exactly as a throttled one is, and the turn continues.
  ///     </summary>
  [Fact]
  public async Task ARejectedHead_IsDropped_AndTheNextEntryServes()
  {
    ScriptedProvider head = new(A, new Reply("ModelRejected", null));
    ScriptedProvider backup = new(B, new Reply(null, "backup answer"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
      [ModelVendors.Cloudflare] = backup,
    });
    Agent agent = Build([Entry(A), Entry(B, ModelVendors.Cloudflare)], resolver, head, new Conversation());

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("backup answer", result.Value);
    Assert.Equal([B], backup.Asked);
  }

  /// <summary>A refused credential STOPS the turn: the entry is not dropped, the walk does not
  ///     advance, and the failure carries the provider's own text.
  ///     </summary>
  [Fact]
  public async Task ARefusedCredential_StopsTheTurn_AndIsNotDropped()
  {
    Conversation conversation = new();
    ScriptedProvider head = new(A, new Reply("ProviderUnauthorized", null));
    ScriptedProvider backup = new(B, new Reply(null, "never served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.Cloudflare] = head,
      [ModelVendors.OpenRouter] = backup,
    });
    Agent agent = Build([Entry(A, ModelVendors.Cloudflare), Entry(B)], resolver, backup, conversation);
    List<string> drops = [];

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnModelDropped: (model, _, _, _) => drops.Add(model)),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderUnauthorized", result.Error.Code);
    // The provider's own text is carried, never replaced by an invented message.
    Assert.Contains("alpha/model could not serve", result.Error.Message, StringComparison.Ordinal);
    Assert.Empty(drops);
    Assert.Empty(backup.Asked);
    Assert.Equal([A], head.Asked);
  }

  /// <summary>A transient fault is retried on the SAME model by the retry policy, so the entry
  ///     is never dropped and the next entry is never reached.
  ///     </summary>
  [Fact]
  public async Task ATransientFault_IsRetriedOnTheSameModel_AndTheWalkDoesNotAdvance()
  {
    ScriptedProvider head = new(A, new Reply("ProviderError", null), new Reply(null, "recovered"));
    ScriptedProvider backup = new(B, new Reply(null, "never served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
      [ModelVendors.Cloudflare] = backup,
    });
    Agent agent = Build([Entry(A), Entry(B, ModelVendors.Cloudflare)], resolver, head, new Conversation());
    List<string> drops = [];

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnModelDropped: (model, _, _, _) => drops.Add(model)),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("recovered", result.Value);
    Assert.Equal([A, A], head.Asked);
    Assert.Empty(backup.Asked);
    Assert.Empty(drops);
  }

  /// <summary>A spent list fails ONCE, naming every model attempted and the reason each gave,
  ///     and the list itself is unchanged by that failure.
  ///     </summary>
  [Fact]
  public async Task ASpentList_FailsOnce_NamingEveryModelAttempted()
  {
    Conversation conversation = new();
    ScriptedProvider head = new(A, new Reply("RateLimited", null));
    ScriptedProvider middle = new(B, new Reply("RateLimited", null));
    ScriptedProvider last = new(C, new Reply("RateLimited", null));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
      [ModelVendors.Cloudflare] = middle,
    });
    // Two vendors, three entries: the third entry shares the OpenRouter transport, which is
    // what a session listing two models from one vendor and one from another looks like.
    SessionModelPreferences preferences = new()
    {
      Entries = [Entry(A), Entry(B, ModelVendors.Cloudflare), Entry(C)],
    };
    Agent agent = new(head, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          TurnRetryDelay = (_, _) => Task.CompletedTask,
          ModelWalk = new ModelWalkSource(preferences, ModelVendors.OpenRouter, resolver),
        });
    _ = last;

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    // The last attempt's own code is carried, so a reader that classifies by code still sees
    // what the final model said.
    Assert.Equal("RateLimited", result.Error.Code);
    // ONE message names every model attempted and the reason each gave.
    Assert.Contains("3 model(s) were attempted", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(A, result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(B, result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(C, result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("RateLimited", result.Error.Message, StringComparison.Ordinal);
    // Each entry was attempted exactly once: the head served the first and third entries.
    Assert.Equal([A, C], head.Asked);
    Assert.Equal([B], middle.Asked);
  }

  /// <summary>An entry that names no vendor falls back to the session's recorded binding, which
  ///     is what every selection written before vendors existed means.
  ///     </summary>
  [Fact]
  public async Task AnEntryWithNoVendor_FallsBackToTheSessionsBinding()
  {
    ScriptedProvider head = new(A, new Reply(null, "served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = head,
    });
    Agent agent = Build([Entry(A)], resolver, head, new Conversation());

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal([(ModelVendors.OpenRouter, A)], resolver.Resolved);
  }

  /// <summary>A vendor with no transport is a drop with its own reason, and the turn serves its
  ///     other vendor instead of failing whole.
  ///     </summary>
  [Fact]
  public async Task AnEntryWhoseVendorHasNoTransport_IsDropped_AndTheNextEntryServes()
  {
    ScriptedProvider backup = new(B, new Reply(null, "other vendor served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = backup,
    });
    Agent agent = Build([Entry(A, ModelVendors.Cloudflare), Entry(B)], resolver, backup,
        new Conversation());
    List<(string Model, string Code)> drops = [];

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnModelDropped: (model, _, code, _) => drops.Add((model, code))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("other vendor served", result.Value);
    (string model, string code) = Assert.Single(drops);
    Assert.Equal(A, model);
    Assert.Equal("ProviderNotConfigured", code);
  }

  /// <summary>An empty list is a named failure before any request is sent, never an empty walk.
  ///     </summary>
  [Fact]
  public async Task AnEmptyList_FailsBeforeAnyRequest()
  {
    ScriptedProvider provider = new(A, new Reply(null, "never served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = provider,
    });
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          TurnRetryDelay = (_, _) => Task.CompletedTask,
          ModelWalk = new ModelWalkSource(new SessionModelPreferences(), ModelVendors.OpenRouter,
              resolver),
        });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(ModelWalk.NoModelsCode, result.Error.Code);
    Assert.Empty(provider.Asked);
  }

  /// <summary>NO TURN SENDS MORE THAN ONE MODEL IN ONE REQUEST: every attempt's config names
  ///     exactly one model, whatever the session's list holds.
  ///     </summary>
  [Fact]
  public async Task EveryAttempt_NamesExactlyOneModel()
  {
    List<ModelConfig> seen = [];
    ScriptedProvider head = new(A, new Reply("RateLimited", null));
    ScriptedProvider backup = new(B, new Reply(null, "served"));
    ScriptedResolver resolver = new(new Dictionary<string, IModelProvider>
    {
      [ModelVendors.OpenRouter] = new RecordingProvider(seen, head),
      [ModelVendors.Cloudflare] = new RecordingProvider(seen, backup),
    });
    Agent agent = Build([Entry(A), Entry(B, ModelVendors.Cloudflare)], resolver, head,
        new Conversation());

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, seen.Count);
    Assert.Equal([A, B], seen.Select(config => config.ModelId));
  }

  /// <summary>A provider that records the config each attempt carried and forwards to another.
  ///     </summary>
  private sealed class RecordingProvider(List<ModelConfig> seen, IModelProvider inner) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      seen.Add(config);
      return inner.SendStreamingAsync(config, request, onContentDelta, onReasoningDelta, ct);
    }
  }

  /// <summary>An unwired agent keeps the single-provider path: the walk is absent, so the
  ///     provider the agent was built with sends and no resolution happens.
  ///     </summary>
  [Fact]
  public async Task AnUnwiredAgent_SendsItsOwnProvider_AndResolvesNothing()
  {
    ScriptedProvider provider = new(A, new Reply(null, "single model"));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { TurnRetryDelay = (_, _) => Task.CompletedTask });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal([A], provider.Asked);
  }
}
