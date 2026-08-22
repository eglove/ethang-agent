namespace eThangAgent.ToolDomain;

/// <summary>One matching line plus its surrounding context window.</summary>
public sealed record SearchMatch(string Path, int LineNumber, IReadOnlyList<string> Lines);

/// <summary>A bounded search page. <paramref name="Truncated"/> means files remained
/// unscanned when the result cap was reached; truncation granularity is whole files.</summary>
public sealed record FileSearch(IReadOnlyList<SearchMatch> Matches, bool Truncated, int FilesScanned);
