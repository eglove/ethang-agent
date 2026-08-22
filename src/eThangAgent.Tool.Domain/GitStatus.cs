namespace eThangAgent.ToolDomain;

/// <summary>One entry of <c>git status --porcelain</c>. Code is the two-char XY column; Path is the rest of the line.</summary>
public sealed record GitStatusEntry(string Code, string Path);

/// <summary>Working-tree status of a repository, grouped porcelain-style.</summary>
public sealed record GitStatus(
    string Branch,
    IReadOnlyList<GitStatusEntry> Staged,
    IReadOnlyList<GitStatusEntry> Unstaged,
    IReadOnlyList<string> Untracked);
