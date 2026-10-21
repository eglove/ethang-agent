using System.Text.Json;

namespace eThangAgent.Transport.ACL;

/// <summary>One root turn sent to a conversation host (spec D6): the user's input plus
///     the seed the app already holds, so the host can run the turn without the app
///     resending a whole transcript per message.
/// <para>Equality is STRUCTURAL over the seed (named decision): the compiler-generated
/// record equality compares <see cref="Seed"/> by REFERENCE, so a request that survived
/// a wire round trip would never equal the one that was sent, and a caller comparing
/// decoded requests would silently always see "different". A null seed (a peer that
/// omitted the field) compares as empty.</para></summary>
public sealed record RootTurnRequest(string TurnId, string Input, IReadOnlyList<string> Seed, long Generation = 0)
{
  public bool Equals(RootTurnRequest? other)
      => other is not null
          && string.Equals(TurnId, other.TurnId, StringComparison.Ordinal)
          && string.Equals(Input, other.Input, StringComparison.Ordinal)
          && Generation == other.Generation
          && (Seed ?? []).SequenceEqual(other.Seed ?? [], StringComparer.Ordinal);

  public override int GetHashCode()
  {
    HashCode hash = default;
    hash.Add(TurnId, StringComparer.Ordinal);
    hash.Add(Input, StringComparer.Ordinal);
    hash.Add(Generation);
    foreach (string entry in Seed ?? [])
    {
      hash.Add(entry, StringComparer.Ordinal);
    }

    return hash.ToHashCode();
  }
}

/// <summary>One event streamed back for a root turn. Kinds are exactly `delta`,
///     `notice`, `tool`, and `settled` - the surface the session view already renders.
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
  /// <summary>The four event kinds the session view renders; anything else is a
  ///     protocol error, not a forward-compatible unknown.</summary>
  public static readonly IReadOnlySet<string> EventKinds = new HashSet<string>(StringComparer.Ordinal)
  {
    "delta", "notice", "tool", "settled",
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
