using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>In-memory IGitWorktreeAccess: a dictionary of name -> WorktreeInfo plus
///     per-call recording, with a configurable failure injected ahead of each
///     operation so tool-level pass-through of seam error codes can be pinned.</summary>
internal sealed class FakeGitWorktreeAccess : IGitWorktreeAccess
{
  private readonly Dictionary<string, WorktreeInfo> _worktrees = new(StringComparer.Ordinal);

  /// <summary>When set, the next matching operation returns this failure instead of
  ///     its normal outcome; the tool must surface the code verbatim.</summary>
  public DomainError? FailOnList { get; set; }
  public DomainError? FailOnCreate { get; set; }
  public DomainError? FailOnRemove { get; set; }

  public List<string> ListCalls { get; } = [];
  public List<string> CreateCalls { get; } = [];
  public List<(string RepoRoot, string Name, bool Force)> RemoveCalls { get; } = [];

  public int CreateCallCount => CreateCalls.Count;

  public void Seed(params WorktreeInfo[] worktrees)
  {
    foreach (WorktreeInfo w in worktrees)
    {
      _worktrees[w.Name] = w;
    }
  }

  public Task<Result<IReadOnlyList<WorktreeInfo>>> ListAsync(string repoRoot, CancellationToken ct = default)
  {
    ListCalls.Add(repoRoot);
    if (FailOnList is not null)
    {
      return Task.FromResult(Result.Failure<IReadOnlyList<WorktreeInfo>>(FailOnList));
    }

    IReadOnlyList<WorktreeInfo> snapshot = [.. _worktrees.Values];
    return Task.FromResult(Result.Success(snapshot));
  }

  public Task<Result<WorktreeInfo>> CreateAsync(string repoRoot, string name, CancellationToken ct = default)
  {
    CreateCalls.Add(name);
    if (FailOnCreate is not null)
    {
      return Task.FromResult(Result.Failure<WorktreeInfo>(FailOnCreate));
    }

    WorktreeInfo created = new(name, Path.Combine(repoRoot, name), "refs/heads/" + name, "0000000", IsMain: false, IsDirty: false);
    _worktrees[name] = created;
    return Task.FromResult(Result.Success(created));
  }

  public Task<Result<bool>> RemoveAsync(string repoRoot, string name, bool force, CancellationToken ct = default)
  {
    RemoveCalls.Add((repoRoot, name, force));
    if (FailOnRemove is not null)
    {
      return Task.FromResult(Result.Failure<bool>(FailOnRemove));
    }

    _ = _worktrees.Remove(name);
    return Task.FromResult(Result.Success(true));
  }
}
