using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>A model that refused or throttled a turn is dropped for the rest of the session and
///     the same turn goes out again on what survives.
///     <para>THE DROP IS THE APP'S, because the app owns the model picker and re-sends its list
///     with every turn. The list that travels is the surviving one; the preference the user
///     picked is never rewritten, and a session opened again starts from the whole list.</para></summary>
public class SessionModelDropTests
{
  private const string A = "alpha/model";
  private const string B = "beta/model";
  private const string C = "gamma/model";

  [Fact]
  public async Task ARateLimitedModel_IsDroppedAndTheSameTurnIsResentOnTheSurvivors()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Answers(B, "hi there");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);

    // Exactly two sends: the refused one and the resend on the survivor.
    Assert.Equal(2, host.Sends);
    Assert.Equal([A, B], ModelsOf(host.Requests[0]));
    Assert.Equal([B], ModelsOf(host.Requests[1]));
    // The resend carries the SAME input.
    Assert.Equal("hi", host.Requests[1].Input, StringComparer.Ordinal);
    // One drop line, verbatim, with the singular at one.
    Assert.Contains($"{A} was rate limited; removed for this session - 1 model left",
        session.Transcript.Notices, StringComparer.Ordinal);
    // The status bar states the surviving list, and one model left has no backup to report.
    Assert.Equal([B], session.Status.SurvivingModelIds);
    Assert.Equal(string.Empty, session.Status.BackupCountText);
    // The stored preference is the user's, unmodified.
    Assert.Equal([A, B], preferences.ModelIds);
    Assert.Contains("hi there", session.Transcript.AssistantText, StringComparison.Ordinal);
    // One user entry for the input, however many times the turn was resent.
    UserMessageEntry sent = Assert.Single(session.Transcript.Entries.OfType<UserMessageEntry>());
    Assert.Equal("hi", sent.Text, StringComparer.Ordinal);
  }

  [Fact]
  public async Task EachDrop_InAListOfThree_NamesTheSurvivingCount()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B, C] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Refuses(B, "ModelRejected").Answers(C, "settled");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(3, host.Sends);
    Assert.Equal([A, B, C], ModelsOf(host.Requests[0]));
    Assert.Equal([B, C], ModelsOf(host.Requests[1]));
    Assert.Equal([C], ModelsOf(host.Requests[2]));
    Assert.Contains($"{A} was rate limited; removed for this session - 2 models left",
        session.Transcript.Notices, StringComparer.Ordinal);
    Assert.Contains($"{B} was rejected; removed for this session - 1 model left",
        session.Transcript.Notices, StringComparer.Ordinal);
    Assert.Equal([C], session.Status.SurvivingModelIds);
    Assert.Equal([A, B, C], preferences.ModelIds);
    // The bar still names the head the user picked: a drop changes the list, and the model that
    // served is named by the usage row the host reports.
    Assert.Equal(A, session.Status.ModelId, StringComparer.Ordinal);
    // The turn succeeded, so its outcome is the answer rather than an error line.
    Assert.DoesNotContain(session.Transcript.Notices,
        n => n.StartsWith("Error [", StringComparison.Ordinal));
    Assert.Contains("settled", session.Transcript.AssistantText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WithOneModelLeft_TheTurnEndsOnItsOwnErrorLine()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B, C] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Refuses(B, "ModelRejected").Refuses(C, "RateLimited");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);

    // The third attempt carries the last model; when it refuses there is nothing left to try,
    // so no fourth request is made and the error line is the turn's answer.
    Assert.Equal(3, host.Sends);
    Assert.Equal([C], ModelsOf(host.Requests[2]));
    Assert.Equal("Error [RateLimited]: the model RateLimited the request.",
        session.Transcript.Notices[^1], StringComparer.Ordinal);
  }

  [Fact]
  public async Task ATransientFailure_NeverDropsAModel()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "ProviderError").Answers(B, "never served");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);

    // A transient fault is the domain's retry loop, not the app's drop: one send, list intact.
    Assert.Equal(1, host.Sends);
    Assert.Equal([A, B], session.Status.SurvivingModelIds);
    Assert.Equal("· 1 backup", session.Status.BackupCountText, StringComparer.Ordinal);
    Assert.DoesNotContain(session.Transcript.Notices, n => n.Contains("removed for this session",
        StringComparison.Ordinal));
  }

  [Fact]
  public async Task RePickingAModelList_ClearsEveryDrop()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Answers(B, "first").Answers(A, "second");
    AgentSessionViewModel session = Session(host, preferences);
    await session.SubmitAsync("hi").ConfigureAwait(true);
    Assert.Equal([B], session.Status.SurvivingModelIds);

    session.ApplyModelChoice([A, B]);
    await session.SubmitAsync("again").ConfigureAwait(true);

    // The user's own list is the whole list again, and the next turn carries it.
    Assert.Equal([A, B], session.Status.SurvivingModelIds);
    Assert.Equal("· 1 backup", session.Status.BackupCountText, StringComparer.Ordinal);
    Assert.Equal([A, B], ModelsOf(host.Requests[2]));
  }

  [Fact]
  public async Task ATurnServedByABackup_KeepsTheDropForLaterTurns()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Answers(B, "first").Answers(A, "second");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);
    await session.SubmitAsync("again").ConfigureAwait(true);

    // The refusing model is not retried on every later turn of the session.
    Assert.Equal([B], ModelsOf(host.Requests[2]));
    Assert.Equal(3, host.Sends);
  }

  [Fact]
  public async Task ReopeningTheSession_StartsFromTheWholeStoredList()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel first = new();
    _ = first.Refuses(A, "RateLimited").Answers(B, "first");
    AgentSessionViewModel dropped = Session(first, preferences);
    await dropped.SubmitAsync("hi").ConfigureAwait(true);
    Assert.Equal([B], dropped.Status.SurvivingModelIds);

    ScriptedTurnChannel reopened = new();
    _ = reopened.Answers(A, "answer one").Answers(B, "answer two");
    AgentSessionViewModel session = Session(reopened, preferences);
    await session.SubmitAsync("hi").ConfigureAwait(true);

    // A drop lives only in the session object, so the reopened tab serves the stored list.
    Assert.Equal([A, B], session.Status.SurvivingModelIds);
    Assert.Equal([A, B], ModelsOf(reopened.Requests[0]));
  }

  [Fact]
  public async Task AllButTheLastModelRefuse_EndsTheTurnWithoutASecondRequestForTheDeadOne()
  {
    SessionModelPreferences preferences = new() { ModelIds = [A, B] };
    ScriptedTurnChannel host = new();
    _ = host.Refuses(A, "RateLimited").Refuses(B, "RateLimited");
    AgentSessionViewModel session = Session(host, preferences);

    await session.SubmitAsync("hi").ConfigureAwait(true);

    // Two sends: A refused and left, then B refused with nothing behind it. No third send repeats
    // a model this session already dropped.
    Assert.Equal(2, host.Sends);
    Assert.Equal([A, B], ModelsOf(host.Requests[0]));
    Assert.Equal([B], ModelsOf(host.Requests[1]));
    Assert.Equal("Error [RateLimited]: the model RateLimited the request.",
        session.Transcript.Notices[^1], StringComparer.Ordinal);
    Assert.Equal([B], session.Status.SurvivingModelIds);
  }

  [Fact]
  public void TheDropObject_HoldsIdsInStoredOrderAndEachOnlyOnce()
  {
    SessionModelDrops drops = new();
    string[] stored = [A, B, C];

    Assert.True(drops.Drop(B));
    Assert.False(drops.Drop(B));
    Assert.Equal(1, drops.Count);
    Assert.Equal([A, C], drops.Surviving(stored));
    // An id the stored list does not name survives nothing: the stored list is unchanged.
    Assert.True(drops.Drop("delta/model"));
    Assert.Equal([A, C], drops.Surviving(stored));

    drops.Clear();

    Assert.Equal(0, drops.Count);
    Assert.Equal(stored, drops.Surviving(stored));
  }

  /// <summary>The model list one request carried to the host, decoded out of its carrier.</summary>
  private static IReadOnlyList<string> ModelsOf(RootTurnRequest request)
  {
    Result<SessionModelPreferences> decoded =
        SessionModelPreferencesJson.Deserialize(Assert.IsType<string>(request.ModelPreferences));
    Assert.True(decoded.IsSuccess, decoded.Error?.Message);
    return decoded.Value.ModelIds ?? [];
  }

  /// <summary>One session over the scripted host. The turn runner records any invocation and
  ///     answers in-process: the app must never run the turn itself.</summary>
  private static AgentSessionViewModel Session(ScriptedTurnChannel host, SessionModelPreferences preferences)
      => new(
          new RecordingTurnRunner().RunAsync,
          new RootSessionLifecycle(new TestFixtures.StubStore()),
          AgentId.NewId(),
          new Conversation(),
          provider: "OpenRouter",
          modelId: "test/model",
          new AgentSessionViewModelOptions
          {
            WorkspaceRoot = @"C:\work\model-drop",
            RootTurnChannel = host,
            ModelPreferences = preferences,
          });
}

/// <summary>A host that answers or refuses according to the model a request asks for FIRST, so a
///     test observes the model list on the wire deciding the outcome. Every request is kept, so a
///     test can read which list each send carried.</summary>
internal sealed class ScriptedTurnChannel : IRootTurnChannel
{
  private readonly Queue<(string? Answer, DomainError? Refusal)> _script = new();
  private readonly Dictionary<string, DomainError> _refusals = [with(StringComparer.Ordinal)];
  private readonly Dictionary<string, string> _answerText = [with(StringComparer.Ordinal)];

  /// <summary>Every request this host received, in send order.</summary>
  public List<RootTurnRequest> Requests { get; } = [];

  /// <summary>How many turns the app sent.</summary>
  public int Sends => Requests.Count;

  /// <summary>Scripts the model a request names FIRST to fail ONCE with <paramref name="code"/>: the
  ///     next send that asks for it is refused, and a later send is answered as scripted.</summary>
  public ScriptedTurnChannel Refuses(string modelId, string code)
  {
    _refusals[modelId] = new DomainError(code, $"the model {code} the request.");
    return this;
  }

  /// <summary>Scripts the model a request names FIRST to serve it, answering <paramref name="answer"/>.</summary>
  public ScriptedTurnChannel Answers(string modelId, string answer)
  {
    _answerText[modelId] = answer;
    return this;
  }

  public Task<Result<string>> SendRootTurnAsync(RootTurnRequest request,
      Action<RootTurnEvent> onEvent, CancellationToken ct = default)
      => Task.FromResult(ct.IsCancellationRequested
          ? Result.Failure<string>(new DomainError("TurnCancelled", "the turn was stopped."))
          : Answer(request, onEvent));

  /// <summary>This host's answer to one request: the scripted send, else the verdict it refuses
  ///     this request's head model with, else the answer that model gives.</summary>
  private Result<string> Answer(RootTurnRequest request, Action<RootTurnEvent> onEvent)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    Requests.Add(request);
    if (_script.Count > 0)
    {
      (string? scripted, DomainError? failure) = _script.Dequeue();
      return failure is { } refused ? Result.Failure<string>(refused)
          : Settle(scripted!, request, onEvent);
    }

    string head = HeadModelOf(request);
    return RefusalOf(head) ?? AnswerOf(head, request, onEvent);
  }

  /// <summary>The verdict this model refuses with, or null when the test scripted none.</summary>
  private Result<string>? RefusalOf(string head)
      => _refusals.Remove(head, out DomainError? error) ? Result.Failure<string>(error) : null;

  /// <summary>The answer the scripted model gives, or a loud failure naming the model this test
  ///     forgot to script.</summary>
  private Result<string> AnswerOf(string head, RootTurnRequest request, Action<RootTurnEvent> onEvent)
  {
    return _answerText.TryGetValue(head, out string? answer)
        ? Settle(answer, request, onEvent)
        : throw new InvalidOperationException(
            $"the app asked model '{head}', which this host has no script for.");
  }

  private static Result<string> Settle(string answer, RootTurnRequest request,
      Action<RootTurnEvent> onEvent)
  {
    onEvent(new RootTurnEvent(request.TurnId, "delta", answer, request.Generation));
    onEvent(new RootTurnEvent(request.TurnId, "settled", answer, request.Generation));
    return Result.Success(answer);
  }

  /// <summary>The model a request asks for first, read out of its preferences carrier.</summary>
  private static string HeadModelOf(RootTurnRequest request)
  {
    Result<SessionModelPreferences> decoded = SessionModelPreferencesJson.Deserialize(
        Assert.IsType<string>(request.ModelPreferences));
    Assert.True(decoded.IsSuccess, decoded.Error?.Message);
    return Assert.IsType<IReadOnlyList<string>>(decoded.Value.ModelIds, exactMatch: false)[0];
  }

  public List<string> InterruptedTurnIds { get; } = [];

  public void InterruptRootTurn(string turnId) => InterruptedTurnIds.Add(turnId);

  public void SteerRootTurn(string turnId, string text, Guid messageId)
  {
    // This host serves no steering: the drop tests never steer a live turn.
  }

  public Task<Result<bool>> SteerAgentAsync(AgentId agentId, string text, Guid messageId)
      => Task.FromResult(Result.Success(true));

  public void SendSessionResponse(string requestId, string? sessionId, string? errorCode, string? errorMessage)
  {
    // This host asks for no session: the drop tests never answer one.
  }

  /// <summary>Reconnects this host is asked for, so a drop test can prove one was never
  ///     attempted for a purely model-side failure.</summary>
  public int Reconnects { get; private set; }

  /// <summary>The verdict a reconnect answers with. Null = the host came back.</summary>
  public DomainError? ReconnectRefusal { get; set; }

  public Task<Result<bool>> ReconnectAsync(CancellationToken ct = default)
  {
    Reconnects++;
    return Task.FromResult(ReconnectRefusal is { } refusal
        ? Result.Failure<bool>(refusal)
        : Result.Success(true));
  }
}
