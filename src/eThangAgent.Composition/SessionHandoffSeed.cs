using eThangAgent.ConversationDomain;

namespace eThangAgent.Composition;

/// <summary>What a compaction handoff seeds the new session with (issue #234). The
///     summary rides as a System message flagged <see cref="Message.IsSummary"/>, which is
///     the same shape an in-place compaction leaves behind — so the model reads it as a
///     summary of earlier work rather than as user voice, and every surface that already
///     understands a summary message understands this one.
///     <para>WHY THE PREFIX IS NOT THE COMPACTION ONE: an in-place compaction's summary says
///     "earlier messages were compacted", which is FALSE for a handoff — nothing was
///     compacted, the earlier conversation is intact in the previous session and resumable.
///     A summary line that misstates its own provenance is a lie the model would reason
///     from.</para>
///     <para>An absent or blank summary seeds NOTHING (null): a plain launch must not carry
///     an empty summary message, which would spend context saying nothing.</para></summary>
public static class SessionHandoffSeed
{
  /// <summary>The verbatim first line of a handoff summary message.</summary>
  public const string Prefix =
      "[Conversation summary — this session continues from an earlier conversation.]";

  /// <summary>The seed for <paramref name="handoffSummary"/>, or null when there is no
  ///     summary to seed (a plain launch).</summary>
  public static IReadOnlyList<Message>? Build(string? handoffSummary)
      => string.IsNullOrWhiteSpace(handoffSummary)
          ? null
          : [new Message(Role.System, Prefix + "\n" + handoffSummary, DateTimeOffset.UtcNow, IsSummary: true)];
}
