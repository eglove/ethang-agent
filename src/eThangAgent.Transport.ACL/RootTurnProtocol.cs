using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>One image the user attached to a turn (issue #20): the sniffed media type and
///     its base64 payload, carried so the host can rebuild the same MessagePart the app's
///     transcript already shows.</summary>
public sealed record RootTurnImage(string MediaType, string Base64Data);

/// <summary>One app-supplied message that must reach the host's conversation BEFORE the
///     turn's user input (spec D6). The app owns two such mutations - a slash invocation's
///     System line and a ! command's model-facing line - and both are CONVERSATION
///     mutations, so they travel as one rather than being re-derived host-side (the host
///     has no skill catalog and no command runner, and inventing either would be a second
///     implementation of the app's own surface).</summary>
public sealed record RootTurnPreTurnMessage(string Role, string Text);

/// <summary>One host→app request to start a NEW session in the workspace (issue #234).
///     Emitted as a <c>session-request</c> root-turn event while the new_session tool runs
///     host-side; the app creates the session, opens it in the requesting tab, and answers
///     over the <c>session-response</c> envelope kind. HandoffSummary is the compaction
///     handoff's summary text — null for a plain launch.</summary>
public sealed record SessionRequestPayload(string RequestId, string Prompt, string? HandoffSummary);

/// <summary>One root turn sent to a conversation host (spec D6): the user's input, the
///     images attached to it, the app-supplied messages that precede it, and the seed the app
///     already holds, so the host can run the turn without the app resending a whole
///     transcript per message.
/// <para>Equality is STRUCTURAL over every collection member (named decision): the
///     compiler-generated record equality compares them by REFERENCE, so a request that
///     survived a wire round trip would never equal the one that was sent, and a caller
///     comparing decoded requests would silently always see "different". A null member (a
///     peer that omitted the field) compares as empty.</para></summary>
public sealed record RootTurnRequest(string TurnId, string Input, IReadOnlyList<string> Seed, long Generation = 0)
{
  /// <summary>Images the user attached to THIS turn (issue #20). They ride the request
  ///     because the app is where they were staged: the host has no clipboard and no file
  ///     drop, so an image that did not travel here would be silently discarded while the
  ///     transcript still showed it attached.</summary>
  public IReadOnlyList<RootTurnImage> Images { get; init; } = [];

  /// <summary>App-supplied messages the host appends to its conversation BEFORE the user
  ///     input (slash invocation lines, ! command lines). Empty for an ordinary turn.</summary>
  public IReadOnlyList<RootTurnPreTurnMessage> PreTurn { get; init; } = [];

  /// <summary>The app's LIVE model preferences, as opaque JSON (the
  ///     <c>SessionModelPreferencesJson</c> wire form), or null for an ordinary turn.
  ///     <para>WHY THE TURN CARRIES IT (2026-10-22): the model picker lives in the app and
  ///     the conversation's agent lives in the host process, so a choice that did not
  ///     travel never reached the model that served the turn - every host turn resolved
  ///     from the persisted pin (the bootstrap routing id, i.e. "nothing selected") and
  ///     served the fallback while the app displayed the user's pick. Transport carries the
  ///     string verbatim and NEVER interprets it; the host decodes it.</para></summary>
  public string? ModelPreferences { get; init; }

  /// <summary>The idle window the APP will enforce on this turn, or null when the app
  ///     does not name one.
  ///     <para>WHY THE TURN CARRIES IT (2026-10-22): the app fails a turn whose host goes
  ///     SILENT for this long, and the host is the only side that knows whether it is
  ///     busy or dead. A long tool call (an exec script, a subagent wait) produces no
  ///     events at all, so silence meant "busy" and the app reported a HEALTHY host as
  ///     unaddressable - then abandoned the turn while the host kept running it, which
  ///     held the turn gate and bricked every later turn. The host now heartbeats inside
  ///     this window (<c>progress</c> events), so the window measures liveness instead of
  ///     busyness. The app owns the policy; the host is told the number it must beat.</para></summary>
  public TimeSpan? IdleTimeout { get; init; }

  /// <summary>Structural equality over the scalars AND every collection member. The
  ///     collection half is delegated so this method stays one readable expression (the
  ///     repository's complexity ceiling) - the comparison itself is unchanged.</summary>
  public bool Equals(RootTurnRequest? other)
      => other is not null
          && string.Equals(TurnId, other.TurnId, StringComparison.Ordinal)
          && string.Equals(Input, other.Input, StringComparison.Ordinal)
          && Generation == other.Generation
          && string.Equals(ModelPreferences, other.ModelPreferences, StringComparison.Ordinal)
          && Nullable.Equals(IdleTimeout, other.IdleTimeout)
          && CollectionsEqual(other);

  private bool CollectionsEqual(RootTurnRequest other)
      => (Seed ?? []).SequenceEqual(other.Seed ?? [], StringComparer.Ordinal)
          && (Images ?? []).SequenceEqual(other.Images ?? [])
          && (PreTurn ?? []).SequenceEqual(other.PreTurn ?? []);

  public override int GetHashCode()
  {
    HashCode hash = default;
    hash.Add(TurnId, StringComparer.Ordinal);
    hash.Add(Input, StringComparer.Ordinal);
    hash.Add(Generation);
    hash.Add(ModelPreferences, StringComparer.Ordinal);
    hash.Add(IdleTimeout);
    foreach (string entry in Seed ?? [])
    {
      hash.Add(entry, StringComparer.Ordinal);
    }

    foreach (RootTurnImage image in Images ?? [])
    {
      hash.Add(image);
    }

    foreach (RootTurnPreTurnMessage message in PreTurn ?? [])
    {
      hash.Add(message);
    }

    return hash.ToHashCode();
  }
}

/// <summary>One event streamed back for a root turn. Kinds are exactly `delta`,
///     `reasoning`, `notice`, `tool`, `settled`, `progress`, `session-request`, and
///     `context` - the first five are the surface the session view renders; `progress` is
///     the LIVENESS HEARTBEAT
///     (2026-10-22): the host emits it when a turn has been quiet, so the app can tell
///     "the host is busy" from "the host stopped answering" instead of failing a healthy
///     long turn; `session-request` is the host's app-ward request to start a NEW session
///     in the workspace (issue #234), emitted while the new_session tool runs host-side;
///     `context` carries the session's context accounting (issue #255) as a
///     <see cref="RootTurnContextPayload"/> JSON object, so the status bar's CTX line
///     advances for a host-run turn exactly as it did in-process; `usage` carries one
///     recorded provider call's spend (issue #24) as a <see cref="RootTurnUsagePayload"/>
///     JSON object, so the status bar's session cost advances the same way.
/// <para>PROTOCOL REQUIREMENT FOR HOST IMPLEMENTERS: <see cref="Generation"/> MUST be echoed
///     verbatim from the <see cref="RootTurnRequest"/> that produced the event. The app mints
///     a fresh generation per turn and delivers an event only when BOTH the turn id and the
///     generation match the live turn, so a host that omits or invents this value has every
///     event silently dropped and its turns fail on the idle bound. The echo is what makes a
///     reused turn id safe: a settle from a timed-out predecessor carries the OLD generation
///     and is ignored instead of completing the new turn with the old payload.</para></summary>
public sealed record RootTurnEvent(string TurnId, string Kind, string Payload, long Generation = 0);


/// <summary>The root-turn wire codec. Strict at both boundaries (spec error contract):
///     a missing turn id or an unknown event kind is a named protocol error, never a
///     silently dropped envelope.</summary>
public static class RootTurnProtocol
{
  /// <summary>The event kinds the wire carries: the five the session view renders,
  ///     `progress` the liveness heartbeat, `session-request` the host's app-ward
  ///     request to start a NEW session (issue #234), and `context` the session's context
  ///     accounting (issue #255), and `usage` one recorded provider call's spend (issue
  ///     #24); anything else is a protocol error, not a forward-compatible unknown.</summary>
  public static readonly IReadOnlySet<string> EventKinds = new HashSet<string>(StringComparer.Ordinal)
  {
    "delta", "reasoning", "notice", "tool", "settled", "progress", "session-request", "context",
    "usage",
  };

  public static string SerializeRequest(RootTurnRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);
    return JsonSerializer.Serialize(request);
  }

  public static RootTurnRequest ParseRequest(string json)
  {
    RootTurnRequest? parsed = JsonSerializer.Deserialize<RootTurnRequest>(json);
    return parsed is null || string.IsNullOrWhiteSpace(parsed.TurnId)
        ? throw new InvalidOperationException("root turn request is missing its turn id.")
        : parsed;
  }

  public static string SerializeEvent(RootTurnEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    return JsonSerializer.Serialize(evt);
  }

  public static RootTurnEvent ParseEvent(string json)
  {
    RootTurnEvent? parsed = JsonSerializer.Deserialize<RootTurnEvent>(json);
    return parsed is null || string.IsNullOrWhiteSpace(parsed.TurnId) || !EventKinds.Contains(parsed.Kind)
        ? throw new InvalidOperationException(DescribeEventFault(parsed))
        : parsed;
  }

  /// <summary>Names WHICH boundary failed: a missing turn id and an unknown kind are
  ///     distinct protocol faults and must not collapse into one message.</summary>
  private static string DescribeEventFault(RootTurnEvent? parsed)
      => parsed is null || string.IsNullOrWhiteSpace(parsed.TurnId)
          ? "root turn event is missing its turn id."
          : "unknown root turn event kind '" + parsed.Kind + "'.";
}
