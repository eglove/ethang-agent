namespace eThangAgent.ToolDomain;

/// <summary>One entry of <c>git status --porcelain</c>. Code is the two-char XY column; Path is the rest of the line.</summary>
public sealed record GitStatusEntry(string Code, string Path);

/// <summary>The commit HEAD points at, as the workbench's git view renders it: the
///     abbreviated hash and the subject line. A repository with no commits yet
///     (fresh <c>git init</c>) has no HEAD commit, and says so with
///     <see cref="GitStatus.HeadCommit"/> being null rather than an empty string —
///     'no commits' and 'a commit with an empty subject' are different facts.</summary>
public sealed record GitHeadCommit(string Hash, string Subject);

/// <summary>Working-tree status of a repository, grouped porcelain-style.
///     <para><see cref="HeadCommit"/> and <see cref="Upstream"/> are the display facts the
///     workbench's git view needs and the model-facing status tool ignores; both are
///     optional so a repository without commits or without a tracking branch reports
///     the absence honestly instead of fabricating a value.</para></summary>
public sealed record GitStatus(
    string Branch,
    IReadOnlyList<GitStatusEntry> Staged,
    IReadOnlyList<GitStatusEntry> Unstaged,
    IReadOnlyList<string> Untracked,
    GitHeadCommit? HeadCommit = null,
    string? Upstream = null);
