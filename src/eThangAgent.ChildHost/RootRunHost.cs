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
///     <para>PROTOCOL REQUIREMENT: every emitted event echoes the request's
///     <see cref="RootTurnRequest.Generation"/> VERBATIM. The app mints a fresh generation
///     per turn and delivers an event only when both the turn id and the generation match
///     the live turn, so a host that omits or invents this value has every event silently
///     dropped and its turns fail on the idle bound.</para>
///     <para>The request's <see cref="RootTurnRequest.Seed"/> is deliberately NOT applied:
///     the host's conversation is the container's own, hydrated from the shared database by
///     the session factory. Replaying the app's seed onto it would double-hydrate the
///     transcript, which corrupts resume.</para></summary>
public sealed class RootRunHost
{
  private readonly IServiceProvider _services;

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
      // A rejected turn never starts: no event is streamed and nothing settles, because
      // the app has not begun a turn it could complete.
      return Result.Failure<string>(new DomainError("InvalidMessage", "a turn needs a non-empty input."));
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
        result.IsSuccess
            ? result.Value
            : "Error [" + result.Error.Code + "]: " + result.Error.Message);
    return result;
  }

  /// <summary>Emits one event, echoing the request's generation verbatim (the protocol
  ///     requirement above — the single place the echo is applied, so no emission path can
  ///     forget it).</summary>
  private static void Emit(RootTurnRequest request, Action<RootTurnEvent> onEvent,
      string kind, string payload)
      => onEvent(new RootTurnEvent(request.TurnId, kind, payload, request.Generation));
}
