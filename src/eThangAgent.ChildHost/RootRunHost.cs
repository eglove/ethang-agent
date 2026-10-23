using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.ChildHost;

/// <summary>The host's ROOT turn runner (spec D6): the conversation's own agent lives HERE,
///     not in the app. A turn is the same command the app used to run inline — the
///     difference is which process executes it. Events stream back AS THEY HAPPEN, so the
///     app renders the turn exactly as before.
///     <para>EVENT MAPPING (decided once, documented verbatim so a reader never guesses):
///     <c>OnContentDelta</c> becomes <c>delta</c>; <c>OnReasoningDelta</c> becomes
///     <c>reasoning</c> (issue #245 - the app renders the model's streaming thoughts);
///     <c>OnToolCall</c> and <c>OnToolResult</c> become <c>tool</c> (payload
///     <c>call &lt;name&gt; &lt;n&gt;/&lt;total&gt; &lt;arguments&gt;</c> and a JSON
///     <see cref="RootTurnToolResultPayload"/> carrying name, status, title, summary, and
///     the FULL content - issue #245: the legacy summary-only shape rendered every result
///     as "ok"); <c>OnSystemMessage</c> and the handler's model-resolution notice become
///     <c>notice</c>; the turn's completion - success OR failure - becomes exactly one
///     <c>settled</c> event carrying either the answer or
///     <c>Error [&lt;code&gt;]: &lt;message&gt;</c>; <c>OnContextUpdate</c> becomes
///     <c>context</c> (a <see cref="RootTurnContextPayload"/> JSON object - issue #255,
///     the status bar's CTX line). Iteration ends and compaction outcomes have no wire
///     kind and are not forwarded.</para>
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
  /// <summary>The longest the heartbeat waits between two liveness events, and the
  ///     interval it falls back to when the app names no idle window. A generous window
  ///     must not turn into a heartbeat flood, and a turn that IS streaming needs no
  ///     proof of life at all.</summary>
  private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

  /// <summary>The shortest the heartbeat may fire: below this the wire carries more
  ///     heartbeat than turn, and the app's own idle window would be too tight to
  ///     distinguish a heartbeat from a lost frame anyway.</summary>
  private static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(1);

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
  ///     settles leaves its caller waiting on the idle bound.
  ///     <para>THE LIVENESS HEARTBEAT (2026-10-22): the app fails a turn whose host goes
  ///     SILENT for its idle window, and a long tool call (an exec script, a subagent
  ///     wait) produces NO events at all — so silence meant "busy", the app reported a
  ///     HEALTHY host as unaddressable, and then abandoned the turn while this process
  ///     kept running it. That abandoned turn held the turn gate, so every later turn
  ///     queued behind it and died the same way: the session was unusable from then on.
  ///     The heartbeat removes the false positive — a <c>progress</c> event whenever the
  ///     turn has been quiet — and it starts BEFORE the gate wait, because a turn QUEUED
  ///     behind another turn is silent too.</para></summary>
  public async Task<Result<string>> RunTurnAsync(
      RootTurnRequest request, Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    TurnEmitter emitter = new(request, onEvent);
    using TurnHeartbeat heartbeat = new(emitter, HeartbeatInterval(request.IdleTimeout));
    try
    {
      if (string.IsNullOrWhiteSpace(request.Input))
      {
        // The turn never starts, but it still SETTLES: the app's outcome comes from the
        // settled event, so a silent return here reports a transport fault instead of this
        // typed refusal.
        Result<string> rejected = Result.Failure<string>(
            new DomainError("InvalidMessage", "a turn needs a non-empty input."));
        emitter.Emit("settled", DescribeFailure(rejected.Error));
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
        return SettleCancelled(emitter);
      }

      try
      {
        return await RunTurnCoreAsync(request, emitter, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return SettleCancelled(emitter);
      }

      finally
      {
        _ = _turnGate.Release();
      }
    }
    finally
    {
      // The turn is over: the heartbeat must not speak for it any more. This runs BEFORE
      // the heartbeat is disposed, so no progress event can follow the settle.
      emitter.Complete();
      // The new-session channel (issue #234) rides THIS turn only: disconnected on every
      // exit path — success, failure, cancellation, and the pre-flight refusals — so a
      // pending session-request never outlives the turn that could carry its answer.
      DisconnectSessionChannel();
    }
  }

  /// <summary>The heartbeat interval: a quarter of the idle window the app will enforce,
  ///     clamped to a sane range.
  ///     <para>The window is the app's policy, so the app names it on the request and the
  ///     host only has to beat it. A quarter leaves a three-heartbeat margin before the
  ///     app gives up, so one lost frame is never fatal; the ceiling keeps a generous
  ///     window from turning into a heartbeat flood.</para></summary>
  private static TimeSpan HeartbeatInterval(TimeSpan? idleTimeout)
  {
    if (idleTimeout is not { } idle || idle <= TimeSpan.Zero)
    {
      return DefaultHeartbeatInterval;
    }

    TimeSpan quarter = idle / 4;
    TimeSpan high = quarter < DefaultHeartbeatInterval ? quarter : DefaultHeartbeatInterval;
    return high < MinimumHeartbeatInterval ? MinimumHeartbeatInterval : high;
  }

  /// <summary>One turn, executed under the turn gate: hydrate, run, settle.</summary>
  private async Task<Result<string>> RunTurnCoreAsync(RootTurnRequest request,
      TurnEmitter emitter, CancellationToken ct)
  {
    await HydrateAsync(request, ct).ConfigureAwait(false);

    if (ConsumeSeedRefusal())
    {
      // The seed is only consulted when the store had nothing, so the refusal surfaces HERE
      // with a typed settle rather than at the top of the turn (fix round 3, finding 2). The
      // flag is CONSUMED (fix round 4, finding 1): it describes one turn, not the runner.
      Result<string> refused = Result.Failure<string>(new DomainError("InvalidSeed",
          "a root turn seed may carry at most one exchange (two lines); a longer seed carries "
          + "no roles and would be silently mislabelled."));
      emitter.Emit("settled", DescribeFailure(refused.Error));
      return refused;
    }

    ApplyPreTurn(request);

    // The app's live model preferences arrive on the REQUEST (2026-10-22): the picker
    // lives in the app and this process owns the agent, so a choice that did not travel
    // here was invisible to the resolver - every turn served the persisted pin, which the
    // bootstrap stamps with the routing id (i.e. "nothing selected"). Applied BEFORE the
    // handler resolves, and a malformed carrier is a typed settle, never a silent fallback.
    if (!ApplyModelPreferences(request))
    {
      Result<string> refused = Result.Failure<string>(new DomainError("InvalidModelPreferences",
          "the root turn's model-preferences carrier could not be read; the turn is refused "
          + "rather than served by a model the user did not choose."));
      emitter.Emit("settled", DescribeFailure(refused.Error));
      return refused;
    }

    // The new-session channel (issue #234) rides the LIVE turn: connected here, before
    // the handler runs, and disconnected in RunTurnAsync's finally. A container without
    // the channel (a test rig) skips silently — the port is an optional capability.
    if (_services.GetService(typeof(NewSessionRequestChannel)) is NewSessionRequestChannel channel)
    {
      channel.Connect(payload => emitter.Emit("session-request", payload));
    }

    SendMessageCommandHandler handler = _services.GetRequiredService<SendMessageCommandHandler>();
    TurnCallbacks callbacks = new(
        OnContentDelta: text => emitter.Emit("delta", text),
        // Reasoning deltas stream as their own wire kind (issue #245): the app renders
        // them as the same ReasoningEntry the in-process path produced. Dropped before,
        // the chat showed no thinking at all for every host turn.
        OnReasoningDelta: text => emitter.Emit("reasoning", text),
        OnToolCall: (name, arguments, position, total) => emitter.Emit("tool",
            "call " + name + " " + position + "/" + total + " " + arguments),
        // The result payload is a JSON object (issue #245): the legacy text shape carried
        // only the summary line ("ok"), so the app's tool cards showed OK instead of what
        // the tool actually did. Name, status, the rich title, the summary, and the FULL
        // content travel; the app renders the content in the card body.
        OnToolResult: (name, summary, content, isError, rich) => emitter.Emit("tool",
            JsonSerializer.Serialize(new RootTurnToolResultPayload(name, isError ? "error" : "ok",
                rich?.Title, summary, content))),
        // The context snapshot (issue #255): the loop fires this after every provider
        // response and after a compaction, and the accountant lives HERE - so a
        // snapshot that did not travel left the app's CTX line frozen for every
        // host-run turn (only the in-process path ever reached Status.SetContext).
        OnContextUpdate: snapshot => emitter.Emit("context", RootTurnContextPayload.Serialize(snapshot)),
        OnSystemMessage: text => emitter.Emit("notice", text));

    Result<string> result;
    try
    {
      result = await handler
          .Handle(new SendMessageCommand(request.Input, BuildImageParts(request)), callbacks,
              text => emitter.Emit("notice", text), ct)
          .ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // CLEAR ON THE FAULT EXIT (issue #234 fix round, finding 4): a handler FAULT (an
      // escaping exception) must not leave HandoffPending set — the flag would fire one
      // turn late, on the next turn's end. The emit itself runs on the returned-Result
      // path below; a CANCELLED turn drops the handoff by design (the session stays
      // resumable; the next turn may hand off again).
      if (_services.GetService(typeof(RootAgentHolder)) is RootAgentHolder faultHolder
          && faultHolder.Current is { HandoffPending: true } faultAgent)
      {
        faultAgent.ClearHandoffPending();
      }

      throw;
    }

    // The handoff emit (issue #234) happens BEFORE the settle: the app's tab
    // replacement runs after the settle drains, so the request must reach it while the
    // turn is still live — and the await gives the app the new session's id first.
    await EmitHandoffIfPendingAsync(emitter, ct).ConfigureAwait(false);

    emitter.Emit("settled",
        result.IsSuccess ? result.Value : DescribeFailure(result.Error));
    return result;
  }

  /// <summary>The compaction handoff's emit (issue #234): when the resolved root agent
  ///     reports <c>HandoffPending</c>, emit a session-request through the
  ///     SAME channel the new_session tool uses — an EMPTY prompt means seed-only launch
  ///     (no auto first turn) — and await the app's answer BEFORE the settle, so the app
  ///     holds the new session's id before the tab replacement. A failure is swallowed
  ///     (the turn's own outcome is unaffected; the app surfaces launch failures itself).
  ///     The pending flag is consumed after acting. The channel is an optional capability:
  ///     a container without it (a test rig) skips silently.
  ///     <para>THE SUMMARY TRAVELS ON THE AGENT (issue #234): the handoff outcome did NOT
  ///     compact the conversation, so there is no summary message in it to mine — the
  ///     summary rides <c>HandoffSummary</c> from the outcome.</para></summary>
  internal async Task EmitHandoffIfPendingAsync(TurnEmitter emitter, CancellationToken ct)
  {
    if (_services.GetService(typeof(RootAgentHolder)) is not RootAgentHolder holder
        || holder.Current is not { HandoffPending: true } agent)
    {
      return;
    }

    string? summary = agent.HandoffSummary;
    try
    {
      // A pending handoff with NO summary must not emit a seedless launch (issue #234
      // fix round, finding 5): the summary IS the new session's seed, so a null one is
      // an anomaly — skip the emit, surface the anomaly as a notice, and still consume
      // the flag.
      if (summary is null)
      {
        emitter.Emit("notice",
            "[context handoff anomaly: a handoff was pending but carried no summary; no new session was launched.]");
        return;
      }

      if (_services.GetService(typeof(NewSessionRequestChannel)) is NewSessionRequestChannel channel)
      {
        // Awaited (bounded by the turn's own token) BEFORE the settle: the app has the
        // id before the tab replacement. A failure is swallowed — the turn's own
        // outcome is unaffected; the app surfaces launch failures itself.
        _ = await channel.RequestAsync(Guid.NewGuid().ToString("N"), prompt: "",
            handoffSummary: summary, ct).ConfigureAwait(false);
      }
    }
#pragma warning disable CA1031 // Named decision: the handoff emit is best-effort — a failed launch never fails the completed turn.
    catch (Exception)
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031
    finally
    {
      agent.ClearHandoffPending();
    }
  }

  /// <summary>Appends the app-supplied messages that must precede this turn's user input
  ///     (a slash invocation's System line, a ! command's model-facing line). They arrive on
  ///     the REQUEST because they are conversation mutations the app already made: the host
  ///     has no skill catalog and no command runner, so re-deriving them here would be a
  ///     second implementation of the app's own surface.
  ///     <para>These use the conversation's Add* methods DELIBERATELY, unlike hydration: they
  ///     are genuinely new messages this turn introduces, so the transcript sink must persist
  ///     them (a hydrated message, by contrast, is one the store already holds). They are
  ///     appended after hydration and before the handler builds its agent, so exactly one
  ///     sink subscription observes each.</para></summary>
  private void ApplyPreTurn(RootTurnRequest request)
  {
    foreach (RootTurnPreTurnMessage message in request.PreTurn ?? [])
    {
      if (string.Equals(message.Role, "system", StringComparison.Ordinal))
      {
        Conversation.AddSystemMessage(message.Text);
      }
      else
      {
        // An unknown role is a PROTOCOL fault, not a silent default: mislabelling a message
        // as user voice would put words in the user's mouth.
        throw new InvalidOperationException(
            "root turn pre-turn message carries an unknown role '" + message.Role + "'.");
      }
    }
  }

  /// <summary>Applies the app's live model preferences (carried opaquely on the request)
  ///     onto this container's own <see cref="SessionModelPreferences"/> singleton, which
  ///     the root resolver and the child spawner read on every turn. Returns false when the
  ///     carrier is present but unreadable - the caller refuses the turn with a typed settle
  ///     instead of serving a model the user did not choose.
  ///     <para>A turn with NO carrier leaves the container's preferences untouched (a
  ///     headless or test host that resolves on its own keeps doing so).</para></summary>
  private bool ApplyModelPreferences(RootTurnRequest request)
  {
    if (string.IsNullOrWhiteSpace(request.ModelPreferences))
    {
      return true;
    }

    Result<SessionModelPreferences> decoded = SessionModelPreferencesJson.Deserialize(request.ModelPreferences);
    if (!decoded.IsSuccess || _services.GetService(typeof(SessionModelPreferences)) is not SessionModelPreferences live)
    {
      return false;
    }

    live.CopyFrom(decoded.Value);
    return true;
  }

  /// <summary>Rebuilds the turn's attached images into the MessageParts the handler takes,
  ///     or null when the turn carries none (the legacy text-only shape). The app is where
  ///     images were staged, so they travel on the request; a turn that dropped them would
  ///     show the user an attached image the model never received.</summary>
  private static IReadOnlyList<MessagePart>? BuildImageParts(RootTurnRequest request)
  {
    IReadOnlyList<RootTurnImage> images = request.Images ?? [];
    return images.Count == 0
        ? null
        : [.. images.Select(image => new MessagePart.ImagePart(image.MediaType, image.Base64Data))];
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
      if (_seedRefused)
      {
        ClearLatch();
      }
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

  /// <summary>Reads AND clears the per-turn seed refusal, so it can never outlive the turn
  ///     that set it.</summary>
  private bool ConsumeSeedRefusal()
  {
    bool refused = _seedRefused;
    _seedRefused = false;
    return refused;
  }

  /// <summary>Clears the hydration latch so the next turn hydrates again. Used by the
  ///     PER-TURN refusal: a refused turn must cost the turn, never the runner.</summary>
  private void ClearLatch()
  {
    lock (_hydrationGate)
    {
      _hydration = null;
    }
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
      // PER-TURN refusal (fix round 4, finding 1). The flag is read by the turn that set it
      // and then cleared; the latch is cleared here too, so the NEXT turn hydrates normally
      // instead of being refused forever. Round 3 returned normally with both still set,
      // which bricked the runner - even for seedless turns.
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

    // THE STORE PATH NEEDS THE SAME GUARD AS THE SEED PATH (fix round 4, finding 2): a store
    // that IGNORES the token completes its read after the latch was cleared and a
    // replacement hydration began, and seeding here would duplicate the whole prefix. The
    // check must be AFTER the read, because that is when abandonment becomes observable.
    ct.ThrowIfCancellationRequested();

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
  private static Result<string> SettleCancelled(TurnEmitter emitter)
  {
    Result<string> cancelled = Result.Failure<string>(
        new DomainError("TurnCancelled", "the turn was cancelled."));
    emitter.Emit("settled", DescribeFailure(cancelled.Error));
    return cancelled;
  }

  /// <summary>The wire form of a failure: the app renders this line verbatim.</summary>
  private static string DescribeFailure(DomainError? error)
      => error is null
          ? "Error [Unknown]: the turn failed without an error."
          : "Error [" + error.Code + "]: " + error.Message;

  /// <summary>Disconnects the new-session channel at turn end (issue #234). The channel
  ///     is an optional capability — a container without it (a test rig) skips silently.
  ///     Pending requests fail as SessionRequestUnreachable: the turn that could carry
  ///     them to the app is gone.</summary>
  private void DisconnectSessionChannel()
  {
    if (_services.GetService(typeof(NewSessionRequestChannel)) is NewSessionRequestChannel channel)
    {
      channel.Disconnect();
    }
  }

  /// <summary>Completes one pending session-request with the app's answer (issue #234).
  ///     The ChildHostServer's session-response handler calls this on the resolved root
  ///     runner. Unknown requestIds are ignored by the channel.</summary>
  public void CompleteSessionRequest(string requestId, Result<string> outcome)
  {
    if (_services.GetService(typeof(NewSessionRequestChannel)) is NewSessionRequestChannel channel)
    {
      channel.Complete(requestId, outcome);
    }
  }

  /// <summary>Releases the turn gate. Disposing a runner with a turn in flight is a host
  ///     shutdown; the in-flight turn observes its own cancellation, never a disposed gate.</summary>
  public void Dispose() => _turnGate.Dispose();

  /// <summary>One turn's event channel: it stamps the request's generation on every event
  ///     (the protocol requirement above — the single place the echo is applied, so no
  ///     emission path can forget it), forwards to the caller's callback, and records when
  ///     the turn last spoke. That last fact is the LIVENESS HEARTBEAT's input: a turn
  ///     that has been quiet for the heartbeat interval is still running, and the host
  ///     says so instead of letting the app read silence as death.
  ///     <para>Thread-safe by construction: the turn's own thread and the heartbeat's
  ///     timer thread both emit.</para></summary>
  // Internal (not private) so the handoff emit can surface the null-summary anomaly
  // notice through the turn's own emitter (issue #234 fix round, finding 5).
  internal sealed class TurnEmitter(RootTurnRequest request, Action<RootTurnEvent> onEvent)
  {
    private readonly Lock _gate = new();
    private DateTimeOffset _lastEmission = DateTimeOffset.UtcNow;
    private bool _completed;

    /// <summary>True once the turn is over: the heartbeat stops speaking for it.</summary>
    public bool IsCompleted()
    {
      lock (_gate)
      {
        return _completed;
      }
    }

    /// <summary>Emits one event for this turn and stamps the moment it went out.</summary>
    public void Emit(string kind, string payload)
    {
      lock (_gate)
      {
        _lastEmission = DateTimeOffset.UtcNow;
      }

      onEvent(new RootTurnEvent(request.TurnId, kind, payload, request.Generation));
    }

    /// <summary>How long the turn has been quiet.</summary>
    public TimeSpan QuietFor(DateTimeOffset now)
    {
      lock (_gate)
      {
        return now - _lastEmission;
      }
    }

    /// <summary>Marks the turn as over. Called after the settle, so the heartbeat can
    ///     never emit a progress event after the answer.</summary>
    public void Complete()
    {
      lock (_gate)
      {
        _completed = true;
      }
    }
  }

  /// <summary>The LIVENESS HEARTBEAT (2026-10-22): a timer that emits a <c>progress</c>
  ///     event whenever the turn has been quiet for the heartbeat interval.
  ///     <para>WHY IT EXISTS: the app fails a turn whose host goes silent for the idle
  ///     window, and a long tool call emits nothing at all — so a healthy host working on
  ///     a long script was reported as unaddressable, and the abandoned turn kept the
  ///     host's turn gate, which bricked every later turn in the session. The heartbeat
  ///     makes the idle window mean what it says: the host stopped answering.</para>
  ///     <para>The heartbeat is a TIMER, so it keeps ticking while the turn is blocked
  ///     inside a tool. It never faults the turn: any failure is swallowed (named
  ///     decision, CA1031) because a heartbeat that cannot be sent is a lost frame, not a
  ///     reason to fail a turn that is working.</para></summary>
  private sealed class TurnHeartbeat : IDisposable
  {
    private readonly Timer _timer;

    public TurnHeartbeat(TurnEmitter emitter, TimeSpan interval)
    {
      ArgumentNullException.ThrowIfNull(emitter);
      _timer = new Timer(_ => Tick(emitter, interval), null, interval, interval);
    }

    /// <summary>One heartbeat tick: a quiet turn says it is alive, a finished turn says
    ///     nothing, and a turn that is streaming needs no proof of life.</summary>
    private static void Tick(TurnEmitter emitter, TimeSpan interval)
    {
      try
      {
        if (emitter.IsCompleted())
        {
          return;
        }

        if (emitter.QuietFor(DateTimeOffset.UtcNow) >= interval)
        {
          emitter.Emit("progress", "the turn is still running.");
        }
      }
#pragma warning disable CA1031 // Named decision: the heartbeat must never fault a turn.
      catch (Exception)
      {
        // A heartbeat that cannot be sent is a lost frame, not a failed turn. The app
        // still has the idle bound and the ceiling to end a turn that really is stuck.
      }
#pragma warning restore CA1031
    }

    /// <summary>Stops the heartbeat. The timer is disposed, so no tick follows the turn;
    ///     a tick already in flight is harmless — the emitter is completed by then, so
    ///     it emits nothing.</summary>
    public void Dispose() => _timer.Dispose();
  }
}
