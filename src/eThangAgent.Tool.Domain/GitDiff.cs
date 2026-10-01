namespace eThangAgent.ToolDomain;

/// <summary>Aggregate line counts from <c>git diff --numstat</c>. Binary files count as 0/0.</summary>
public sealed record GitDiffStats(int Files, int Additions, int Deletions);

/// <summary>A diff patch, delivered in full.</summary>
public sealed record GitDiff(GitDiffStats Stats, string Patch);
