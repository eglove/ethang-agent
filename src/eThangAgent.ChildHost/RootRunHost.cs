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
public sealed class RootRunHost : IDisposable
{
  private readonly IServiceProvider _services;

  // TURN SERIALIZATION (fix round 2, finding 1). Turns are dispatched concurrently by the
  // host (round 1 moved them off the serve loop), but a conversation is a SEQUENCE: two
  // turns interleaving would append their messages out of order and each would see a
  // half-built history. The gate makes a root turn atomic - one at a time, in arrival
  // order - which is exactly the app-side contract the Desktop's busy flag used to give.
  private readonly SemaphoreSlim _turnGate = new(1, 1);

  // HYDRATION SINGLE-FLIGHT (fix round 2, finding 1/8). The latch is set only AFTER a
  // successful load: a cancelled or failed hydration leaves it unset so the next turn
  // retries, instead of leaving the runner permanently empty-headed. The task is shared so
  // concurrent callers never start two reads.
  private readonly Lock _hydrationGate = new();
  private Task? _hydration;
  private volatile bool _seedRefused;

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

    try
    {
      await _turnGate.WaitAsync(ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Same contract the handler's own cancellation path honors: a stop during the wait for
      // the gate fails the turn as TurnCancelled, never as a raw exception across the
      // Task<Result<string>> seam - and it SETTLES (fix round 3, finding 1): the app reads
      // the outcome from the settled event, so silence makes it wait out its whole budget.
      return SettleCancelled(request, onEvent);
    }

    try
    {
      return await RunTurnCoreAsync(request, onEvent, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return SettleCancelled(request, onEvent);
    }

    finally
    {
      _ = _turnGate.Release();
    }
  }

  /// <summary>One turn, executed under the turn gate: hydrate, run, settle.</summary>
  private async Task<Result<string>> RunTurnCoreAsync(RootTurnRequest request,
      Action<RootTurnEvent> onEvent, CancellationToken ct)
  {
    await HydrateAsync(request, ct).ConfigureAwait(false);

    if (_seedRefused)
    {
      // The seed is only consulted when the store had nothing, so the refusal surfaces HERE
      // with a typed settle rather than at the top of the turn (fix round 3, finding 2).
      Result<string> refused = Result.Failure<string>(new DomainError("InvalidSeed",
          "a root turn seed may carry at most one exchange (two lines); a longer seed carries "
          + "no roles and would be silently mislabelled."));
      Emit(request, onEvent, "settled", DescribeFailure(refused.Error));
      return refused;
    }

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
    Task hydration;
    lock (_hydrationGate)
    {
      // The latch is the TASK, and it is cleared on failure below: a cancelled or failed
      // hydration must leave the runner un-hydrated so the NEXT turn retries (fix round 2,
      // findings 1 and 8). Setting a bool before the read was the round-1 defect.
      _hydration ??= HydrateCoreAsync(request.Seed, ct);
      hydration = _hydration;
    }

    try
    {
      await hydration.WaitAsync(ct).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): a hydration that did not complete leaves the latch CLEARED
      // so the next turn tries again; it never leaves a permanently empty conversation.
      lock (_hydrationGate)
      {
        _hydration = null;
      }

      throw;
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>The one hydration attempt: store first, seed second. Runs at most once to
  ///     completion; a failure clears the latch so a later turn retries.</summary>
  private async Task HydrateCoreAsync(IReadOnlyList<string>? seed, CancellationToken ct)
  {
    if (Conversation.Messages.Count > 0)
    {
      return; // the container was seeded with history already (the app-side shape)
    }

    if (await TryHydrateFromStoreAsync(ct).ConfigureAwait(false))
    {
      return;
    }

    // A cancelled or abandoned hydration must NEVER mutate the conversation (fix round 3,
    // finding 3): the latch is cleared on failure and a later turn starts its own hydration,
    // so an orphan that seeded here would duplicate the prefix and land it out of order.
    ct.ThrowIfCancellationRequested();

    // The seed refusal lives HERE, not at the top of the turn (fix round 3, finding 2): the
    // seed is only a FALLBACK, so a long seed must not refuse a turn whose history came from
    // the authoritative store.
    if (seed is { Count: > 2 })
    {
      // Recorded on the runner rather than thrown: the refusal is discovered deep inside the
      // hydration task, and the turn path reads it after awaiting that task.
      _seedRefused = true;
      return;
    }

    HydrateFromSeed(seed);
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
    catch (OperationCanceledException)
    {
      // Cancellation is NOT 'the store has nothing' (fix round 3, finding 3): treating it as
      // empty fell through to the seed path and mutated an abandoned conversation.
      throw;
    }
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Named decision (CA1031): a store that genuinely cannot be read leaves the seed path
      // to run, because a turn with less history is better than a turn that never starts.
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
  ///     store had nothing.
  ///     <para>ROLE INFERENCE IS A HEURISTIC, NOT A CONTRACT. <see cref="RootTurnRequest.Seed"/>
  ///     is a plain list of text lines and carries NO ROLES, so this maps the first line to
  ///     <see cref="Role.User"/> and every later line to <see cref="Role.Assistant"/>. That is
  ///     only truthful for a SINGLE exchange (one user message, one answer). For any longer
  ///     history it would silently relabel every user message after the first as assistant
  ///     output, so the caller refuses a seed longer than two lines instead of approximating
  ///     it (see <see cref="HydrateCoreAsync"/>). A multi-exchange resume needs a
  ///     role-carrying seed field in the protocol; until that exists, a refusal is honest and
  ///     a mislabelled history is a silent quality bug.</para></summary>
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

  /// <summary>Emits the typed cancellation settle and returns it. Both cancellation paths
  ///     (the gate wait and the turn body) go through here, so no cancellation can return
  ///     without telling the app what happened.</summary>
  private static Result<string> SettleCancelled(RootTurnRequest request,
      Action<RootTurnEvent> onEvent)
  {
    Result<string> cancelled = Result.Failure<string>(
        new DomainError("TurnCancelled", "the turn was cancelled."));
    Emit(request, onEvent, "settled", DescribeFailure(cancelled.Error));
    return cancelled;
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

  /// <summary>Releases the turn gate. Disposing a runner with a turn in flight is a host
  ///     shutdown; the in-flight turn observes its own cancellation, never a disposed gate.</summary>
  public void Dispose() => _turnGate.Dispose();
}
