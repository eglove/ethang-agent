using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

// CA1031: the debounce catch-all IS the named resilience decision (issue #348) -
// a transient fault may never kill the watcher loop.
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Event-driven workspace file watcher (issue #348): FileSystemWatcher events,
///     250 ms debounce, and a serialized check gate so overlapping passes cannot diff
///     against the same baseline. A pass emits a raw dirty-path set (added/modified)
///     and deleted-path set; renames surface as a delete plus an add. Excluded paths
///     (DirectWorkspaceSnapshotAccess.ExcludedDirectories) never produce events.
///     <para>Polling sweep (#349) catches missed events; generation counter and re-sync are #350.</para></summary>
public sealed class WorkspaceFileWatcher(
    string workspaceRoot,
    IWorkspaceSnapshotAccess snapshots,
    Func<WorkspaceChangeSet, Task> onPass,
    TimeSpan? pollingInterval = null) : IAsyncDisposable
{
  private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
  private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(5);

  private readonly string _root = workspaceRoot
      ?? throw new ArgumentNullException(nameof(workspaceRoot));
  private readonly IWorkspaceSnapshotAccess _snapshots = snapshots
      ?? throw new ArgumentNullException(nameof(snapshots));
  private readonly Func<WorkspaceChangeSet, Task> _onPass = onPass
      ?? throw new ArgumentNullException(nameof(onPass));
  private readonly TimeSpan _pollingInterval = pollingInterval ?? DefaultPollingInterval;
  private readonly CancellationTokenSource _cts = new();
  private readonly Lock _gate = new();
  private readonly SemaphoreSlim _checkGate = new(1, 1);
  private FileSystemWatcher? _watcher;
  private WorkspaceSnapshot? _baseline;
  private int _checking;
  private bool _started;
  private Task? _sweepLoop;

  /// <summary>True once <see cref="Start"/> has run; exposed for wiring tests.</summary>
  public bool IsStarted { get; private set; }

  /// <summary>Captures the baseline snapshot synchronously, then enables FSW events.
  ///     Idempotent: a second Start is a no-op.</summary>
  public void Start()
  {
    lock (_gate)
    {
      if (_started)
      {
        return;
      }

      _started = true;
      IsStarted = true;
    }

    // Baseline BEFORE enabling events: events arriving during capture are not lost.
    _baseline = Task.Run(() => _snapshots.CaptureAsync(_root, _cts.Token), CancellationToken.None).GetAwaiter().GetResult();

    if (!Directory.Exists(_root))
    {
      return;
    }

    FileSystemWatcher fsw = new(_root, "*")
    {
      IncludeSubdirectories = true,
      NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
          | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
    };
    fsw.Changed += OnEvent;
    fsw.Created += OnEvent;
    fsw.Deleted += OnEvent;
    fsw.Renamed += OnEvent;
    fsw.Error += OnErrored;
    fsw.EnableRaisingEvents = true;
    _watcher = fsw;
    _sweepLoop = Task.Run(() => SweepLoopAsync(), CancellationToken.None);
  }

  private void OnErrored(object sender, ErrorEventArgs e) => ScheduleCheck();

  private void OnEvent(object sender, FileSystemEventArgs e)
  {
    if (IsExcluded(e.FullPath))
    {
      return;
    }

    ScheduleCheck();
  }

  private bool IsExcluded(string fullPath)
  {
    string relative = Path.GetRelativePath(_root, fullPath);
    return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => DirectWorkspaceSnapshotAccess.ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
  }

  private void ScheduleCheck()
  {
    if (Interlocked.Exchange(ref _checking, 1) == 0)
    {
      _ = Task.Run(
          async () =>
          {
            try
            {
              await Task.Delay(Debounce, _cts.Token).ConfigureAwait(false);
              await CheckOnceAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
              // Expected on dispose: the debounce delay is canceled.
            }
            catch
            {
              // Named decision (issue #348): a transient fault never kills the watcher.
            }
            finally
            {
              _ = Interlocked.Exchange(ref _checking, 0);
            }
          },
          CancellationToken.None);
    }
  }

  /// <summary>One polling sweep: same diff as CheckOnceAsync against the owned baseline.</summary>
  public Task SweepOnceAsync(CancellationToken ct = default) => CheckOnceAsync(ct);

  /// <summary>Test hook: simulate a buffer overflow by forcing a sweep.</summary>
  public void SimulateBufferOverflow() => ScheduleCheck();

  private async Task SweepLoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(_pollingInterval, _cts.Token).ConfigureAwait(false);
        await CheckOnceAsync(_cts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch
      {
        // Named decision (#348/#349): transient faults never kill the sweep.
      }
    }
  }

  /// <summary>One check pass: capture a snapshot, diff against the baseline, announce
  ///     a non-empty change set. Concurrent callers serialize on a gate - EVERY caller
  ///     performs its own pass, none is skipped, never concurrent.</summary>
  public async Task CheckOnceAsync(CancellationToken ct = default)
  {
    await _checkGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      WorkspaceSnapshot after = await _snapshots.CaptureAsync(_root, ct).ConfigureAwait(false);
      if (_baseline is null)
      {
        _baseline = after;
        return;
      }

      WorkspaceChangeSet changes = _baseline.DiffTo(after);
      _baseline = after;
      if (!changes.IsEmpty)
      {
        await _onPass(changes).ConfigureAwait(false);
      }
    }
    finally
    {
      _ = _checkGate.Release();
    }
  }

  /// <summary>Stops the watcher. Safe to call from any thread.</summary>
  public async ValueTask DisposeAsync()
  {
    _watcher?.EnableRaisingEvents = false;
    _watcher?.Dispose();

    _checkGate.Dispose();
    await _cts.CancelAsync().ConfigureAwait(false);
    if (_sweepLoop is { } loop)
    {
      try
      {
        await loop.ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // Expected on dispose.
      }
    }

    _cts.Dispose();
  }
}
