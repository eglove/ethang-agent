namespace eThangAgent.ToolDomain;

/// <summary>The result of a write. <c>NormalizedTo</c> is the line-ending convention the
///     bytes actually landed in, so a tool can state it in its annotation line instead
///     of leaving the model to infer it from the file.</summary>
public sealed record FileWriteOutcome(bool Created, long BytesWritten, LineEnding NormalizedTo);
