using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.ChildHost;

/// <summary>The host's ROOT turn runner (spec D6): the conversation's own agent lives HERE,
///     not in the app. A turn is the same command the app used to run inline — the
///     difference is which process executes it. Events stream back AS THEY HAPPEN, so the
///     app renders the turn exactly as before.
///     <para>EVENT MAPPING (decided once, documented verbatim so a reader never guesses):
///     <c>OnContentDelta</c> becomes <c>delta</c>; <c>OnToolCall</c> and <c>OnToolResult</c>
///     become <c>tool</c> (payload <c>call &lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt;</c> and
///     <c>result &lt;name&gt; ok|error &lt;summary&gt;</c>); <c>OnSystemMessage</c> and the
///     handler's model-resolution notice become <c>notice</c>; the turn's completion —
///     success OR failure — becomes exactly one <c>settled</c> event carrying either the
///     answer or <c>Error [&lt;code&gt;]: &lt;message&gt;</c>. Reasoning deltas, iteration ends,
///     context snapshots, and compaction outcomes have no wire kind and are not forwarded.</para>
///     <para>EVERY FAILURE SETTLES, including the pre-flight ones. A rejected turn (empty
///     input) emits its typed <c>settled</c> event before returning: the app reads the
///     outcome from that event, so a silent early return would leave its caller waiting out
///     the whole idle bound and then reporting a TRANSPORT fault for a turn the host simply
///     refused.</para>
///     <para>PROTOCOL REQUIREMENT: every emitted event echoes the request's
///     <see cref="RootTurnRequest.Generation"/> VERBATIM. The app mints a fresh generation
///     per turn and delivers an event only when both the turn id and the generation match
///     the live turn, so a host that omits or invents this value has every event silently
///     dropped and its turns fail on the idle bound.</para>
///     <para>HYDRATION (fix round 1, finding 2): the host's container builds its
///     <see cref="Conversation"/> with NO seed, so before the first turn the runner loads
///     the persisted transcript from the container's store and, when the store has nothing
///     for this session, falls back to the request's <see cref="RootTurnRequest.Seed"/> —
///     the field exists precisely so the host can run a turn without the app resending a
///     whole transcript per message. Hydration happens BEFORE the first turn's agent build,
///     which is the only moment it is safe: the agent subscribes the root transcript sink
///     when it is constructed, so messages added after that point would be re-persisted as
///     if the model had just produced them.</para></summary>
public sealed class RootRunHost
{
  private readonly IServiceProvider _services;
  private readonly Lock _hydrationGate = new();
  private bool _hydrated;

  /// <summary>Builds the runner over a host's own headless container.</summary>
  public RootRunHost(SessionHost host, AgentId rootId, Conversation conversation)
      : this(host?.Services ?? throw new ArgumentNullException(nameof(host)), rootId, conversation)
  {
  }

  /// <summary>Builds the runner over any container that registers the root turn path —
  ///     the shape tests compose. The conversation MUST be the container's own: the agent
  ///     the handler resolves writes into that instance, so a runner pointed at a different
  ///     one would stream nothing and persist nothing while reporting success.</summary>
  public RootRunHost(IServiceProvider services, AgentId rootId, Conversation conversation)
  {
    ArgumentNullException.ThrowIfNull(conversation);
    _services = services ?? throw new ArgumentNullException(nameof(services));
    RootId = rootId;
    Conversation = conversation;

    if (services.GetService(typeof(Conversation)) is Conversation owned
        && !ReferenceEquals(owned, conversation))
    {
      throw new InvalidOperationException(
          "RootRunHost must be given the container's own Conversation; the agent writes into that instance.");
    }

    // Prerequisite (found the hard way): the host never fills RootSessionIdentity, so the
    // container's RootTranscriptSink resolved no root id and was INERT — every host turn
    // would run without persisting a single message. The sink resolves its id lazily and
    // caches the first success, so filling the carrier BEFORE the first turn makes the
    // sink active. An already-filled identity is the app-side factory's value and wins.
    if (services.GetService(typeof(RootSessionIdentity)) is RootSessionIdentity identity
        && identity.Id is null)
    {
      identity.Id = rootId;
    }
  }

  /// <summary>The root session this runner serves.</summary>
  public AgentId RootId { get; }

  /// <summary>The container's conversation, exposed so a host can inspect the history a
  ///     turn will actually see.</summary>
  public Conversation Conversation { get; }

  /// <summary>Runs one turn, streaming its events as they happen. Returns the turn's own
  ///     result so the caller can log or audit it; the app reads the outcome from the
  ///     <c>settled</c> event, which is emitted for failures too — a turn that never
  ///     settles leaves its caller waiting on the idle bound.</summary>
  public async Task<Result<string>> RunTurnAsync(
      RootTurnRequest request, Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    if (string.IsNullOrWhiteSpace(request.Input))
    {
      // The turn never starts, but it still SETTLES: the app's outcome comes from the
      // settled event, so a silent return here reports a transport fault instead of this
      // typed refusal.
      Result<string> rejected = Result.Failure<string>(
          new DomainError("InvalidMessage", "a turn needs a non-empty input."));
      Emit(request, onEvent, "settled", DescribeFailure(rejected.Error));
      return rejected;
    }

    await HydrateAsync(request, ct).ConfigureAwait(false);

    SendMessageCommandHandler handler = _services.GetRequiredService<SendMessageCommandHandler>();
    TurnCallbacks callbacks = new(
        OnContentDelta: text => Emit(request, onEvent, "delta", text),
        OnToolCall: (name, arguments, position, total) => Emit(request, onEvent, "tool",
            "call " + name + " " + position + "/" + total + " " + arguments),
        OnToolResult: (name, summary, content, isError, rich) => Emit(request, onEvent, "tool",
            "result " + name + " " + (isError ? "error" : "ok") + " " + summary),
        OnSystemMessage: text => Emit(request, onEvent, "notice", text));

    Result<string> result = await handler
        .Handle(new SendMessageCommand(request.Input), callbacks,
            text => Emit(request, onEvent, "notice", text), ct)
        .ConfigureAwait(false);

    Emit(request, onEvent, "settled",
        result.IsSuccess ? result.Value : DescribeFailure(result.Error));
    return result;
  }

  /// <summary>Loads the conversation history a turn must see, ONCE, before the first turn.
  ///     The store is authoritative (a resumed session's rows are what the app already
  ///     showed); the request's seed is the fallback for a host whose store has nothing —
  ///     a fresh session whose root row was never written. Both paths run before the agent
  ///     is built, because building it subscribes the transcript sink and anything added
  ///     afterwards would be persisted as freshly produced output.</summary>
  private async Task HydrateAsync(RootTurnRequest request, CancellationToken ct)
  {
    lock (_hydrationGate)
    {
      if (_hydrated)
      {
        return;
      }

      _hydrated = true;
    }

    if (Conversation.Messages.Count > 0)
    {
      return; // the container was seeded with history already (the app-side shape)
    }

    if (await TryHydrateFromStoreAsync(ct).ConfigureAwait(false))
    {
      return;
    }

    HydrateFromSeed(request.Seed);
  }

  /// <summary>Hydrates from the container's store when it has a transcript for this root.
  ///     Best-effort by design: a store that cannot be read leaves the seed path to run,
  ///     because a turn with less history is better than a turn that never starts.</summary>
  private async Task<bool> TryHydrateFromStoreAsync(CancellationToken ct)
  {
    if (_services.GetService(typeof(IAgentStore)) is not IAgentStore store)
    {
      return false;
    }

    Result<IReadOnlyList<Message>> transcript;
    try
    {
      transcript = await store.GetTranscriptAsync(RootId, ct).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): hydration is best-effort — the seed path covers it.
      return false;
    }
#pragma warning restore CA1031 // Do not catch general exception types

    if (!transcript.IsSuccess || transcript.Value.Count == 0)
    {
      return false;
    }

    Conversation.Seed(transcript.Value);
    return true;
  }

  /// <summary>Applies the request's seed — the app's own view of the history — when the
  ///     store had nothing. Roles are inferred from the seed's own shape: the protocol
  ///     carries plain text lines, so the FIRST line is the user's opening message and
  ///     every later line is assistant output, which is exactly the shape a session's
  ///     transcript takes before any tool call.</summary>
  private void HydrateFromSeed(IReadOnlyList<string>? seed)
  {
    if (seed is null || seed.Count == 0)
    {
      return;
    }

    // Seed, never Add*: the Add methods raise the mutation notification the transcript
    // sink subscribes, and a hydrated message is one the store already holds - raising it
    // would append every historical message a second time.
    DateTimeOffset now = DateTimeOffset.UtcNow;
    List<Message> history = [];
    for (int index = 0; index < seed.Count; index++)
    {
      history.Add(new Message(index == 0 ? Role.User : Role.Assistant, seed[index], now));
    }

    Conversation.Seed(history);
  }

  /// <summary>The wire form of a failure: the app renders this line verbatim.</summary>
  private static string DescribeFailure(DomainError? error)
      => error is null
          ? "Error [Unknown]: the turn failed without an error."
          : "Error [" + error.Code + "]: " + error.Message;

  /// <summary>Emits one event, echoing the request's generation verbatim (the protocol
  ///     requirement above — the single place the echo is applied, so no emission path can
  ///     forget it).</summary>
  private static void Emit(RootTurnRequest request, Action<RootTurnEvent> onEvent,
      string kind, string payload)
      => onEvent(new RootTurnEvent(request.TurnId, kind, payload, request.Generation));
}
