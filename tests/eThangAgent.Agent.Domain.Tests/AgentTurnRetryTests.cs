using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A transient provider fault retries the SAME request inside the turn, with a
///     notice before each wait and the bounded schedule <see cref="TurnRetryPolicy"/> owns.
///     Every case substitutes a completed wait through <see cref="AgentOptions.TurnRetryDelay"/>,
///     so the counts here are attempts rather than elapsed time and no test spends clock time.
///     A fake provider replaces the real one entirely, which is what makes the attempt counts
///     exact: the provider ACL's own inner retries are that layer's business and are absent here.</summary>
public class AgentTurnRetryTests
{
  /// <summary>One scripted provider call: the fragments it streams, the code it then fails
  ///     with, and what it answers when it does not fail.</summary>
  private sealed record Attempt(string[] Streamed, string? FailCode, string? Answer, bool EmitsToolCall = false);

  private sealed class ScriptedProvider(params Attempt[] attempts) : IModelProvider
  {
    private int _next;

    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      Calls++;
      Attempt attempt = attempts[Math.Min(_next++, attempts.Length - 1)];
      foreach (string delta in attempt.Streamed)
      {
        onContentDelta?.Invoke(delta);
      }

      if (attempt.FailCode is { } code)
      {
        return Task.FromResult(
            Result.Failure<ModelResponse>(new DomainError(code, "the provider call failed.")));
      }

      ModelResponse response = attempt.EmitsToolCall
          ? new ModelResponse(null, [new ToolCallRequest("c1", "t", "{}")])
          : new ModelResponse(attempt.Answer ?? string.Empty, []);
      return Task.FromResult(Result.Success(response));
    }
  }

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  /// <summary>An agent whose retry waits complete immediately: the seam the schedule is
  ///     observed through, and the reason this suite spends no clock time waiting.</summary>
  private static Agent Build(IModelProvider provider, Conversation conversation,
      IToolRegistry? tools = null, List<TimeSpan>? waits = null)
      => new(provider, conversation, DefaultConfig, tools ?? new ToolRegistry([]),
          new AgentOptions
          {
            TurnRetryDelay = (delay, _) =>
            {
              waits?.Add(delay);
              return Task.CompletedTask;
            },
          });

  /// <summary>Two transient failures then success: three provider calls, two notices, one
  ///     answer. The failed attempts' text never reaches the conversation, so the turn ends
  ///     holding the successful answer alone.</summary>
  [Fact]
  public async Task TransientTwiceThenSuccess_ThreeCalls_TwoNotices_OneAnswer()
  {
    List<string> notices = [];
    Conversation conversation = new();
    ScriptedProvider provider = new(
        new Attempt(["Hello, wor"], "StreamInterrupted", null),
        new Attempt([], "ProviderError", null),
        new Attempt([], null, "Hi there."));
    Agent agent = Build(provider, conversation);

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnStreamRetrying: (attempt, delay, reason) =>
                notices.Add(TurnRetryPolicy.Describe(attempt, delay, reason))),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("Hi there.", result.Value);
    Assert.Equal(3, provider.Calls);
    Assert.Equal(2, notices.Count);
    // The notice names the attempt, its wait in whole seconds, and the reason. The wait is
    // jittered into [delay, 2 x delay], so the printed seconds are a range rather than a
    // fixed number: the first wait is one or two seconds, the second is two to four.
    Assert.StartsWith("[retry 2 of 6] waiting ", notices[0], StringComparison.Ordinal);
    Assert.EndsWith("s: the provider call failed.", notices[0], StringComparison.Ordinal);
    Assert.StartsWith("[retry 3 of 6] waiting ", notices[1], StringComparison.Ordinal);
    Assert.InRange(WholeSeconds(notices[0]), 1, 2);
    Assert.InRange(WholeSeconds(notices[1]), 2, 4);
    // Nothing the failed attempts streamed entered the conversation, and the user's message
    // is there exactly once.
    Assert.Contains(conversation.Messages, m => m.Role is Role.Assistant && m.Content == "Hi there.");
    Assert.DoesNotContain(conversation.Messages, m => m.Content.Contains("Hello", StringComparison.Ordinal));
    _ = Assert.Single(conversation.Messages, m => m.Role is Role.User);
  }

  /// <summary>A provider that never recovers spends exactly six attempts, then fails the turn
  ///     as it always did. Five notices precede the six attempts, and the waits born of the
  ///     shipped schedule stay inside the policy's own bounds: 31 s nominal, at most 62 s at
  ///     worst-case jitter - which is why the attempt cap binds before the total budget.</summary>
  [Fact]
  public async Task AlwaysTransient_SixAttempts_ThenTheTurnFails()
  {
    List<string> notices = [];
    List<TimeSpan> waits = [];
    Conversation conversation = new();
    ScriptedProvider provider = new(new Attempt([], "ProviderError", null));
    Agent agent = Build(provider, conversation, waits: waits);

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnStreamRetrying: (attempt, delay, reason) => notices.Add(reason)),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code, StringComparer.Ordinal);
    Assert.Equal(TurnRetryPolicy.MaxAttempts, provider.Calls);
    Assert.Equal(5, notices.Count);
    Assert.Equal(5, waits.Count);
    TimeSpan spent = waits.Aggregate(TimeSpan.Zero, (total, wait) => total + wait);
    Assert.True(spent >= TimeSpan.FromSeconds(31), $"the waits summed to {spent}.");
    Assert.True(spent <= TimeSpan.FromSeconds(62), $"the waits summed to {spent}.");
    Assert.True(spent <= TurnRetryPolicy.TotalBudget);
    // The failure line is the turn's record, appended once however many attempts it took.
    Message failure = Assert.Single(conversation.Messages, m => m.Role is Role.System);
    Assert.Contains("Error [ProviderError]:", failure.Content, StringComparison.Ordinal);
  }

  /// <summary>A model verdict is not a fault a retry can fix, so it costs exactly one call and
  ///     no wait: the turn fails at once and the app's model drop takes over.</summary>
  [Fact]
  public async Task RateLimited_OneCall_NoWait_NoNotice()
  {
    List<string> notices = [];
    List<TimeSpan> waits = [];
    ScriptedProvider provider = new(new Attempt([], "RateLimited", null));
    Agent agent = Build(provider, new Conversation(), waits: waits);

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnStreamRetrying: (attempt, delay, reason) => notices.Add(reason)),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("RateLimited", result.Error.Code, StringComparer.Ordinal);
    Assert.Equal(1, provider.Calls);
    Assert.Empty(waits);
    Assert.Empty(notices);
  }

  /// <summary>A DROP VERDICT IS NOT RETRIED AND DOES NOT SPEND A WAIT: the walk advances on it,
  ///     so a throttled model costs exactly one call and the next entry serves the turn.
  ///     <para>THE DISTINCTION IS THE POINT OF THE WALK: a transient fault is retried on the SAME
  ///     model, and a drop verdict moves on. Reading one as the other would either retry a model
  ///     that will never serve or drop a model that had already recovered.</para></summary>
  [Fact]
  public async Task ADropVerdict_AdvancesTheWalk_WithoutSpendingARetry()
  {
    List<TimeSpan> waits = [];
    List<string> drops = [];
    Conversation conversation = new();
    ScriptedProvider head = new(new Attempt([], "RateLimited", null));
    ScriptedProvider backup = new(new Attempt([], null, "the backup served"));
    Agent agent = new(head, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          TurnRetryDelay = (delay, _) =>
          {
            waits.Add(delay);
            return Task.CompletedTask;
          },
          ModelWalk = new ModelWalkSource(
              new SessionModelPreferences { Entries = [new("test-model"), new("backup-model")] },
              ModelVendors.OpenRouter,
              new OneVendorResolver(new Dictionary<string, IModelProvider>
              {
                ["test-model"] = head,
                ["backup-model"] = backup,
              })),
        });

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(OnModelDropped: (model, _, code, _) => drops.Add($"{model} {code}")),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("the backup served", result.Value);
    Assert.Equal(1, head.Calls);
    Assert.Equal(1, backup.Calls);
    Assert.Empty(waits);
    Assert.Equal(["test-model RateLimited"], drops);
  }

  /// <summary>The resolver a walk needs when one vendor serves every entry: the provider is
  ///     chosen by the config's model id, so each entry reaches its own scripted provider.
  ///     </summary>
  private sealed class OneVendorResolver(Dictionary<string, IModelProvider> byModel)
      : IModelProviderResolver
  {
    public Result<IModelProvider> Resolve(string vendorId, ModelConfig config) =>
        byModel.TryGetValue(config.ModelId, out IModelProvider? provider)
            ? Result.Success(provider)
            : Result.Failure<IModelProvider>(new DomainError("ProviderNotConfigured",
                $"no provider for '{config.ModelId}'."));

    public Result<IModelCatalog> Catalog(string vendorId) =>
        Result.Failure<IModelCatalog>(new DomainError("ProviderNotConfigured", "no catalog."));
  }

  /// <summary>A caller stop that lands during a retry wait ends the turn as a cancellation:
  ///     the wait's OperationCanceledException is surfaced as TurnCancelled, never as a turn
  ///     fault, and no further attempt is made.</summary>
  [Fact]
  public async Task CancelledDuringTheWait_TurnCancelled_NoFurtherCall()
  {
    using CancellationTokenSource cts = new();
    ScriptedProvider provider = new(new Attempt([], "ProviderError", null));
    Conversation conversation = new();
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          TurnRetryDelay = async (_, _) =>
          {
            await cts.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cts.Token);
          },
        });

    Result<string> result = await agent.SendMessage("hi", ct: cts.Token);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.TurnCancelledCode, result.Error.Code, StringComparer.Ordinal);
    Assert.Equal(1, provider.Calls);
    Assert.DoesNotContain(conversation.Messages,
        m => m.Content.Contains(Agent.TurnFailedPrefix, StringComparison.Ordinal));
  }

  /// <summary>The retry costs a caller nothing it did not ask for: with neither observer wired
  ///     the fault is still retried, and nothing is rendered.</summary>
  [Fact]
  public async Task NoObserversWired_StillRetries_AndRendersNothing()
  {
    ScriptedProvider provider = new(
        new Attempt(["partial"], "StreamInterrupted", null),
        new Attempt([], null, "done"));
    Agent agent = Build(provider, new Conversation());

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, provider.Calls);
  }

  /// <summary>The withdrawal precedes the notice: a retry line that arrived first would sit
  ///     above the stale prefix it is about.</summary>
  [Fact]
  public async Task StreamedTextIsWithdrawnBeforeTheNotice()
  {
    List<string> observed = [];
    List<string> screen = [];
    ScriptedProvider provider = new(
        new Attempt(["Hel"], "StreamInterrupted", null),
        new Attempt([], null, "Hi there."));
    Agent agent = Build(provider, new Conversation());

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(
            // The withdrawable text is what the failed attempt put in front of an observer,
            // so the observer is what a rewind is about.
            OnContentDelta: screen.Add,
            OnStreamRewind: () =>
            {
              Assert.Equal(["Hel"], screen);
              observed.Add("rewind");
            },
            OnStreamRetrying: (attempt, delay, reason) => observed.Add("notice")),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["rewind", "notice"], observed);
  }

  /// <summary>One retry wraps ONE provider call, not the turn: an iteration that completed its
  ///     tool call is not repeated when the next iteration's provider call fails transiently.</summary>
  [Fact]
  public async Task TransientFaultInALaterIteration_DoesNotRepeatEarlierWork()
  {
    Conversation conversation = new();
    OkTool tool = new();
    ScriptedProvider provider = new(
        new Attempt([], null, null, EmitsToolCall: true),
        new Attempt([], "ProviderTimeout", null),
        new Attempt([], null, "done"));
    Agent agent = Build(provider, conversation, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
    Assert.Equal(3, provider.Calls);
    Assert.Equal(1, tool.Calls);
    // One tool call, one tool result: the fault neither repeated nor orphaned it.
    _ = Assert.Single(conversation.Messages, m => m.Role is Role.Tool);
  }

  /// <summary>Under the shipped numbers the attempts bind before the budget: five waits of
  ///     1, 2, 4, 8 and 16 s are 31 s nominal and 62 s at worst-case jitter, both inside the
  ///     180 s budget. The budget is therefore the guard that fires only if the cap is raised.</summary>
  [Fact]
  public void UnderTheShippedNumbers_TheAttemptCapBindsBeforeTheBudget()
  {
    TimeSpan worstCase = TimeSpan.Zero;
    TimeSpan nominal = TimeSpan.Zero;
    for (int attempt = 1; TurnRetryPolicy.AllowsRetry(attempt, worstCase,
             TurnRetryPolicy.ComputeDelay(attempt, 1.0)); attempt++)
    {
      nominal += TurnRetryPolicy.ComputeDelay(attempt, 0.0);
      worstCase += TurnRetryPolicy.ComputeDelay(attempt, 1.0);
    }

    // 1 + 2 + 4 + 8 + 16 = 31 s nominal; doubled, the fifth wait is 32 s and the cap holds it
    // at 30, so the worst case is 60 s rather than 62.
    Assert.Equal(TimeSpan.FromSeconds(31), nominal);
    Assert.Equal(TimeSpan.FromSeconds(60), worstCase);
    Assert.True(worstCase < TurnRetryPolicy.TotalBudget);
  }

  /// <summary>The whole-second count a retry notice names.</summary>
  /// <summary>The wait a retry notice names, as the whole seconds it prints.</summary>
  private static int WholeSeconds(string notice) =>
      int.Parse(notice.Split(' ')[5][..^2], System.Globalization.CultureInfo.InvariantCulture);

  private sealed class OkTool : ITool
  {
    public int Calls { get; private set; }

    public ToolDefinition Definition { get; } = new("t", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(new ToolResult("ok", false));
    }
  }
}
