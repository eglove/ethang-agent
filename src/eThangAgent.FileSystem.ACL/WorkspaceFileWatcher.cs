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
///     Polling sweep (#349) catches missed events. Generation counter, pub/sub
///     announcements, and 30 s re-sync (#350) reuse the same baseline diff.</summary>
public sealed class WorkspaceFileWatcher(
    string workspaceRoot,
    IWorkspaceSnapshotAccess snapshots,
    Func<WorkspaceChangeSet, Task> onPass,
    TimeSpan? pollingInterval = null,
    TimeSpan? resyncInterval = null,
    TimeSpan? debounce = null) : IAsyncDisposable, IFileWatcher
{
  private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
  private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan DefaultResyncInterval = TimeSpan.FromSeconds(30);

  private readonly string _root = workspaceRoot
      ?? throw new ArgumentNullException(nameof(workspaceRoot));
  private readonly IWorkspaceSnapshotAccess _snapshots = snapshots
      ?? throw new ArgumentNullException(nameof(snapshots));
  private readonly Func<WorkspaceChangeSet, Task> _onPass = onPass
      ?? throw new ArgumentNullException(nameof(onPass));
  private readonly TimeSpan _debounce = debounce ?? DefaultDebounce;
  private readonly TimeSpan _pollingInterval = pollingInterval ?? DefaultPollingInterval;
  private readonly TimeSpan _resyncInterval = resyncInterval ?? DefaultResyncInterval;
  private readonly List<Func<FileChangeAnnouncement, Task>> _subscribers = [];
  private readonly Lock _subscribersGate = new();
  private long _generation;
  private Task? _resyncLoop;
  private readonly CancellationTokenSource _cts = new();
  private readonly Lock _gate = new();
  private readonly SemaphoreSlim _checkGate = new(1, 1);
  private FileSystemWatcher? _watcher;
  private WorkspaceSnapshot? _baseline;
  private int _checking;
  private bool _started;
  private Task? _sweepLoop;
  private int _disposed;

  /// <summary>True once <see cref="Start"/> has run; exposed for wiring tests.</summary>
  public bool IsStarted { get; private set; }

  /// <summary>Current generation: 0 at start, +1 per pass that found changes.</summary>
  public long Generation => Volatile.Read(ref _generation);

  /// <summary>Subscribes a handler invoked sequentially per changing pass.
  ///     Disposing the returned subscription stops further announcements to it.</summary>
  public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler)
  {
    ArgumentNullException.ThrowIfNull(handler);
    lock (_subscribersGate)
    {
      _subscribers.Add(handler);
    }

    return new Subscription(this, handler);
  }

  private void Unsubscribe(Func<FileChangeAnnouncement, Task> handler)
  {
    lock (_subscribersGate)
    {
      _ = _subscribers.Remove(handler);
    }
  }

  private sealed class Subscription(WorkspaceFileWatcher owner, Func<FileChangeAnnouncement, Task> handler)
      : IDisposable
  {
    private bool _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, true))
      {
        return;
      }

      owner.Unsubscribe(handler);
    }
  }

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
    _resyncLoop = Task.Run(() => ResyncLoopAsync(), CancellationToken.None);
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
              await Task.Delay(_debounce, _cts.Token).ConfigureAwait(false);
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

  /// <summary>One 30 s re-sync pass: the same hash-table diff as a sweep, so a missed
  ///     event is corrected by the next re-sync. One mechanism, two intervals.</summary>
  public Task ResyncOnceAsync(CancellationToken ct = default) => CheckOnceAsync(ct);

  private async Task ResyncLoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(_resyncInterval, _cts.Token).ConfigureAwait(false);
        await CheckOnceAsync(_cts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch
      {
        // Named decision (#348/#349/#350): transient faults never kill the re-sync.
      }
    }
  }

  private async Task AnnounceAsync(WorkspaceChangeSet changes)
  {
    long generation = Interlocked.Increment(ref _generation);
    List<string> paths = [.. changes.Added, .. changes.Modified];
    paths.Sort(StringComparer.Ordinal);
    FileChangeAnnouncement announcement = new(paths, changes.Removed, generation, DateTimeOffset.UtcNow);
    Func<FileChangeAnnouncement, Task>[] subscribers;
    lock (_subscribersGate)
    {
      subscribers = [.. _subscribers];
    }

    foreach (Func<FileChangeAnnouncement, Task> subscriber in subscribers)
    {
      try
      {
        await subscriber(announcement).ConfigureAwait(false);
      }
      catch
      {
        // Named decision (#350): one bad subscriber never blocks others or the next pass.
      }
    }
  }

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
      // The baseline is handed back as the earlier evidence, so a pass over an unchanged
      // tree costs stat calls instead of re-reading and re-hashing every file.
      WorkspaceSnapshot after = await _snapshots.CaptureAsync(_root, _baseline, ct).ConfigureAwait(false);
      if (_baseline is null)
      {
        _baseline = after;
        return;
      }

      WorkspaceChangeSet changes = _baseline.DiffTo(after);
      _baseline = after;
      if (!changes.IsEmpty)
      {
        await AnnounceAsync(changes).ConfigureAwait(false);
        await _onPass(changes).ConfigureAwait(false);
      }
    }
    finally
    {
      _ = _checkGate.Release();
    }
  }

  /// <summary>Stops the watcher. Safe to call from any thread, and idempotent.
  ///     <para>Cancellation comes before the gate is disposed: a pass already inside
  ///     <see cref="CheckOnceAsync"/> releases the gate on its way out, so the gate is disposed
  ///     only once the loops have settled.</para></summary>
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
      return;
    }

    _watcher?.EnableRaisingEvents = false;
    _watcher?.Dispose();
    _watcher = null;

    await _cts.CancelAsync().ConfigureAwait(false);
    await AwaitLoopAsync(_sweepLoop).ConfigureAwait(false);
    await AwaitLoopAsync(_resyncLoop).ConfigureAwait(false);

    _checkGate.Dispose();
    _cts.Dispose();
  }

  /// <summary>Awaits a loop this watcher started, treating its cancellation as the expected
  ///     end of the loop rather than a fault.</summary>
  private static async Task AwaitLoopAsync(Task? loop)
  {
    if (loop is null)
    {
      return;
    }

    try
    {
      await loop.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Expected on dispose.
    }
  }
}
