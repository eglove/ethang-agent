namespace eThangAgent.ToolDomain;

/// <summary>The result of an edit. <c>NormalizedTo</c> is the line-ending convention the
///     replacement text was rewritten into before splicing — the file's own convention,
///     or the project's when the file declares none.</summary>
public sealed record ReplaceOutcome(int Replaced, int NewLineCount, LineEnding NormalizedTo);
