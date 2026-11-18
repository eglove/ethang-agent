using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>An in-memory recent-workspace store: the launcher's view-model is driven through
///     it without a database, and a test can make any operation fail by name.</summary>
internal sealed class FakeRecentWorkspaceStore : IRecentWorkspaceStore
{
  private readonly List<RecentWorkspace> _rows = [];

  /// <summary>When set, every read and write returns this failure instead of acting.</summary>
  public DomainError? Failure { get; set; }

  /// <summary>When set, only <see cref="ForgetAsync"/> fails.</summary>
  public DomainError? ForgetFailure { get; set; }

  public IReadOnlyList<string> Recorded => [.. _rows.Select(r => r.Root)];

  public void Seed(params string[] roots)
  {
    foreach (string root in roots)
    {
      _ = _rows.RemoveAll(r => r.Root == WorkspaceIdentity.Normalize(root));
      _rows.Insert(0, new RecentWorkspace(WorkspaceIdentity.Normalize(root), DateTimeOffset.UtcNow));
    }
  }

  public Task<Result<IReadOnlyList<RecentWorkspace>>> ListAsync(int limit, CancellationToken ct = default)
      => Task.FromResult(Failure is { } failure
          ? Result.Failure<IReadOnlyList<RecentWorkspace>>(failure)
          : Result.Success<IReadOnlyList<RecentWorkspace>>([.. _rows.Take(limit)]));

  public Task<Result<bool>> RecordAsync(string root, CancellationToken ct = default)
  {
    if (Failure is { } failure)
    {
      return Task.FromResult(Result.Failure<bool>(failure));
    }

    Seed(root);
    return Task.FromResult(Result.Success(true));
  }

  public Task<Result<bool>> ForgetAsync(string root, CancellationToken ct = default)
  {
    if (ForgetFailure is { } failure)
    {
      return Task.FromResult(Result.Failure<bool>(failure));
    }

    _ = _rows.RemoveAll(r => r.Root == WorkspaceIdentity.Normalize(root));
    return Task.FromResult(Result.Success(true));
  }
}
