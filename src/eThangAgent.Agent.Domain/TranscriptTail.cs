using eThangAgent.ConversationDomain;

namespace eThangAgent.AgentDomain;

/// <summary>What a transcript looked like at one read: the messages from a given sequence
///     onward, and how many the transcript holds in total.
///     <para>THE COUNT IS THE POINT OF THE TYPE. A reader that follows a growing transcript has
///     to discover whether anything changed, and the cheap way to discover it is to ask for the
///     messages AFTER the last one already rendered. Asking for the whole transcript instead
///     costs bytes proportional to what is already on screen rather than to what arrived — a
///     1,735-message session re-read 4.69 MiB twice a second to learn that nothing had.</para>
///     <para><see cref="Messages"/> is ordered by sequence ascending and is empty when nothing
///     followed <c>fromSeq</c>. <see cref="TotalCount"/> counts every message the transcript
///     holds, so a reader's own cursor can be reconciled against an authoritative number rather
///     than trusted to match after a compaction replaced the rows under it.</para></summary>
/// <param name="Messages">The messages whose sequence is at or above the requested start.</param>
/// <param name="TotalCount">How many messages the transcript holds in total.</param>
public sealed record TranscriptTail(IReadOnlyList<Message> Messages, int TotalCount)
{
  /// <summary>A transcript that ended before the requested start: nothing to render.</summary>
  /// <param name="totalCount">How many messages the transcript holds.</param>
  public static TranscriptTail Empty(int totalCount) => new([], totalCount);
}
