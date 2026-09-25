using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

// CA1031: the sweep and debounce catch-alls ARE the named resilience decision
// (spec #26) - a transient read fault may never kill the reload loop.
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Hot reload (spec #26): watches the configured skill directories and
///     turns directory changes into catalog reloads + announcements. Detection is
///     belt-and-braces: a <see cref="FileSystemWatcher"/> per directory (debounced)
///     PLUS a polling sweep on a fixed interval - FSW misses buffer overflows and
///     atomic-save renames, so polling is the eventually-consistent net. The
///     announce delegate receives the diff (the reloader renders and delivers it);
///     an empty diff announces nothing. Start is idempotent; DisposeAsync stops
///     everything. Inert until Start.</summary>
public sealed class SkillDirectoryWatcher(
    IReloadableSkillCatalog catalog,
    Action<SkillReloadDiff> announce,
    IReadOnlyList<SkillDirectory>? directories = null) : IAsyncDisposable
{
  private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

  private readonly IReloadableSkillCatalog _catalog = catalog
      ?? throw new ArgumentNullException(nameof(catalog));
  private readonly Action<SkillReloadDiff> _announce = announce
      ?? throw new ArgumentNullException(nameof(announce));
  private readonly IReadOnlyList<SkillDirectory> _directories = directories ?? [];
  private readonly CancellationTokenSource _cts = new();
  private readonly List<FileSystemWatcher> _watchers = [];
  private readonly Lock _gate = new();
  private Task? _sweepLoop;
  private int _checking;
  private bool _started;

  /// <summary>True once <see cref="Start"/> has run; exposed for wiring tests.</summary>
  public bool IsStarted { get; private set; }

  /// <summary>Starts the FSW watchers and the polling sweep. Idempotent: a second
  ///     Start is a no-op (the factory calls it on create AND resume paths).</summary>
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

    foreach (string path in _directories.Select(d => d.Path).Where(Directory.Exists))
    {
      FileSystemWatcher fsw = new(path, "*")
      {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
      };
      fsw.Changed += OnChangeEvent;
      fsw.Created += OnChangeEvent;
      fsw.Deleted += OnChangeEvent;
      fsw.Renamed += OnRenameEvent;
      fsw.EnableRaisingEvents = true;
      _watchers.Add(fsw);
    }

    _sweepLoop = Task.Run(() => SweepLoopAsync(), CancellationToken.None);
  }

  private void OnChangeEvent(object sender, FileSystemEventArgs e) => ScheduleCheck();

  private void OnRenameEvent(object sender, RenamedEventArgs e) => ScheduleCheck();

  private void ScheduleCheck()
  {
    // Debounce: coalesce event bursts into one check. A check already in flight
    // makes this one skip; the sweep re-checks on the next interval regardless,
    // so no change can be lost - only delayed.
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
              // Named decision (spec #26): a transient fault never kills the debounce.
            }
            finally
            {
              _ = Interlocked.Exchange(ref _checking, 0);
            }
          },
          CancellationToken.None);
    }
  }

  private async Task SweepLoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(SweepInterval, _cts.Token).ConfigureAwait(false);
        await CheckOnceAsync(_cts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch
      {
        // Named decision (spec #26): the sweep survives any transient fault.
      }
    }
  }

  /// <summary>One reload pass: reload the catalog and announce a non-empty diff.
  ///     Concurrent callers skip while a check is in flight (their change is
  ///     caught by the next sweep or FSW event - never lost, only delayed).</summary>
  public async Task CheckOnceAsync(CancellationToken ct = default)
  {
    if (Interlocked.Exchange(ref _checking, 1) == 1)
    {
      return;
    }

    try
    {
      Result<SkillReloadDiff> diff = await _catalog.ReloadAsync(ct).ConfigureAwait(false);
      if (diff.IsSuccess && (diff.Value.Added.Count > 0 || diff.Value.Removed.Count > 0
          || diff.Value.Changed.Count > 0))
      {
        _announce(diff.Value);
      }
    }
    finally
    {
      _ = Interlocked.Exchange(ref _checking, 0);
    }
  }

  /// <summary>Stops the watchers and the sweep loop. Safe to call from any thread.</summary>
  public async ValueTask DisposeAsync()
  {
    foreach (FileSystemWatcher fsw in _watchers)
    {
      fsw.EnableRaisingEvents = false;
      fsw.Dispose();
    }

    _watchers.Clear();
    await _cts.CancelAsync().ConfigureAwait(false);
    if (_sweepLoop is { } loop)
    {
      try
      {
        await loop.ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // Expected on shutdown: the sweep exits via its token.
      }
    }

    _cts.Dispose();
  }
}
