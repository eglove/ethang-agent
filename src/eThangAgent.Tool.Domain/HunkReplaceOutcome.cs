namespace eThangAgent.ToolDomain;

/// <summary>The result of an atomic multi-hunk edit. <c>ReplacedPerHunk</c> carries one
///     count per hunk IN THE ORDER THE HUNKS WERE GIVEN, so the annotation states what
///     each hunk did. <c>NormalizedTo</c> is the line-ending convention every hunk's
///     replacement was rewritten into before splicing (the file's own).</summary>
public sealed record HunkReplaceOutcome(IReadOnlyList<int> ReplacedPerHunk, int NewLineCount, LineEnding NormalizedTo);
