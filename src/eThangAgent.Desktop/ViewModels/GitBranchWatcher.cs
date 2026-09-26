using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Keeps the status bar's branch display current: watches the workspace's
///     <c>.git</c> directory (branch switches, checkouts, and commits that move
///     HEAD all write there) and re-resolves the branch through
///     <see cref="IGitQueryAccess.GetBranchAsync"/> on every change. Event-driven —
///     no polling loop; the watcher sleeps until git writes. A workspace that is
///     not a git repository leaves the display empty, and a watcher whose git
///     query fails stays silent (a failed probe is not a user-facing event).
///     Dispose stops watching. Debounced: one git gesture can burst several writes,
///     so a short coalescing delay keeps one query per gesture, not one per write.</summary>
internal sealed class GitBranchWatcher : IDisposable
{
  /// <summary>Debounce between a git write and the refresh. Long enough to absorb
  ///     a checkout's burst of writes, short enough to feel instant.</summary>
  internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

  private readonly string _workspaceRoot;
  private readonly IGitQueryAccess _git;
  private readonly StatusViewModel _status;
  private readonly FileSystemWatcher? _watcher;
  private readonly CancellationTokenSource _cancelled = new();
  private readonly Lock _gate = new();
  private Task _refresh = Task.CompletedTask;

  public GitBranchWatcher(string workspaceRoot, IGitQueryAccess git, StatusViewModel status)
  {
    ArgumentNullException.ThrowIfNull(git);
    ArgumentNullException.ThrowIfNull(status);
    _workspaceRoot = workspaceRoot;
    _git = git;
    _status = status;

    string gitDir = Path.Combine(workspaceRoot, ".git");
    if (Directory.Exists(gitDir))
    {
      // The whole .git directory is the watched surface, not HEAD alone: git
      // replaces HEAD through intermediate files on some operations, and a
      // name-filtered watcher misses exactly those.
      FileSystemWatcher watcher = new()
      {
        Path = gitDir,
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        IncludeSubdirectories = true,
        InternalBufferSize = 8192,
      };
      watcher.Changed += OnGitChanged;
      watcher.Created += OnGitChanged;
      watcher.Deleted += OnGitChanged;
      watcher.Renamed += OnGitRenamed;
      // Named decision (CA1031): a workspace whose .git exists but cannot be watched
      // (permissions, network drive) degrades to a one-shot initial resolve instead
      // of crashing the tab open.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
      }
      catch (Exception)
      {
        watcher.Dispose();
        // Live watch unavailable; the initial resolve below still ran.
      }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    // The initial resolve runs regardless: the branch at open time must show even
    // when the live watch could not start.
    RefreshSoon();
  }

  private void OnGitChanged(object sender, FileSystemEventArgs e) => RefreshSoon();

  private void OnGitRenamed(object sender, RenamedEventArgs e) => RefreshSoon();

  /// <summary>Schedules one debounced refresh; overlapping schedules coalesce.</summary>
  private void RefreshSoon()
  {
    lock (_gate)
    {
      if (_refresh.IsCompleted)
      {
        _refresh = RefreshAsync(_cancelled.Token);
      }
    }
  }

  private async Task RefreshAsync(CancellationToken ct)
  {
    try
    {
      await Task.Delay(Debounce, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return;
    }

    Result<string> branch = await _git.GetBranchAsync(_workspaceRoot, ct).ConfigureAwait(false);
    _status.Branch = branch.IsSuccess ? branch.Value : string.Empty;
  }

  public void Dispose()
  {
    _cancelled.Cancel();
    _watcher?.Dispose();
    _cancelled.Dispose();
  }
}
