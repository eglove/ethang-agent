using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One line of a rendered diff, split so the view can colour it without
///     re-parsing: the text, and which of the four kinds of line it is.
///     <para>The kinds are the ones a unified diff actually has - a hunk header, an
///     added line, a removed line, and everything else (context, file headers, index
///     lines). Anything else would be a guess about content git never promised.</para></summary>
internal enum DiffLineKind
{
  Context,
  Hunk,
  Added,
  Removed,
}

/// <summary>One rendered diff line.</summary>
internal sealed record DiffLineViewModel(string Text, DiffLineKind Kind)
{
  public bool IsAdded => Kind == DiffLineKind.Added;

  public bool IsRemoved => Kind == DiffLineKind.Removed;

  public bool IsHunk => Kind == DiffLineKind.Hunk;

  /// <summary>Classifies one raw patch line. Order matters: '+++' and '---' are file
  ///     HEADERS, not an addition and a removal, so they are checked before the
  ///     single-character prefixes they start with.</summary>
  public static DiffLineViewModel For(string line)
  {
    ArgumentNullException.ThrowIfNull(line);
    if (line.StartsWith("@@", StringComparison.Ordinal))
    {
      return new DiffLineViewModel(line, DiffLineKind.Hunk);
    }

    if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
    {
      return new DiffLineViewModel(line, DiffLineKind.Context);
    }

    DiffLineKind kind = line.Length == 0
        ? DiffLineKind.Context
        : line[0] switch
        {
          '+' => DiffLineKind.Added,
          '-' => DiffLineKind.Removed,
          _ => DiffLineKind.Context,
        };
    return new DiffLineViewModel(line, kind);
  }
}

/// <summary>One row of the CHANGES list: the porcelain status code and the path, with
///     the code spelled out the way a person reads it.</summary>
internal sealed record GitChangeRowViewModel(string Code, string Path, string Label)
{
  /// <summary>Builds a row from a porcelain entry. The label is derived from the code
  ///     rather than invented: '??' is new, a rename is a rename, anything else is
  ///     'modified' - the three things a working tree can be.
  ///     <para>A rename's porcelain path is 'old -> new'; the row keeps the whole string
  ///     (that is what git reported) while <see cref="SelectablePath"/> names the path a
  ///     diff can actually be asked for.</para></summary>
  public static GitChangeRowViewModel For(GitStatusEntry entry, bool staged)
  {
    ArgumentNullException.ThrowIfNull(entry);
    return new GitChangeRowViewModel(entry.Code, entry.Path, LabelFor(entry.Code, staged));
  }

  /// <summary>Spells a porcelain code out. The check is per CHARACTER, not per code
  ///     pair: porcelain's two columns are independent (index and worktree), so 'RM' and
  ///     'MR' are both renames and enumerating pairs would silently miss one.</summary>
  private static string LabelFor(string code, bool staged) => code.Trim() switch
  {
    "??" => "new",
    var c when c.Contains('R', StringComparison.Ordinal) => "renamed",
    var c when c.Contains('A', StringComparison.Ordinal) => "added",
    var c when c.Contains('D', StringComparison.Ordinal) => "deleted",
    _ => staged ? "staged" : "modified",
  };

  /// <summary>Builds a row for an untracked path (porcelain lists those without a code).</summary>
  public static GitChangeRowViewModel Untracked(string path)
  {
    ArgumentNullException.ThrowIfNull(path);
    return new GitChangeRowViewModel("??", path, "new");
  }

  /// <summary>The path a diff request can name: a rename's 'old -> new' collapses to the
  ///     NEW path, because that is the file that exists in the worktree.</summary>
  public string SelectablePath
  {
    get
    {
      int arrow = Path.IndexOf(" -> ", StringComparison.Ordinal);
      return arrow < 0 ? Path : Path[(arrow + 4)..];
    }
  }
}

/// <summary>The workbench's Git view (issue #9): branch, working-tree status, and the
///     diff for a selected path, updating as files change.
///     <para>REALTIME SOURCE: a <see cref="FileSystemWatcher"/> over the WORKSPACE ROOT,
///     not over <c>.git</c>. <see cref="GitBranchWatcher"/> watches <c>.git</c> because it
///     only tracks branch changes, and working-tree edits never touch <c>.git</c> - a
///     <c>.git</c>-only watch would miss every file edit, which is the exact thing this
///     view exists to show.</para>
///     <para>EXCLUSIONS are load-bearing, not an optimisation: <c>bin</c>, <c>obj</c>,
///     <c>node_modules</c>, and <c>.git</c> are skipped, because a build writes thousands
///     of files and would otherwise turn one compile into a watcher storm. A change
///     anywhere else coalesces into ONE refresh after <see cref="Debounce"/>, so a
///     multi-file save is one git query, not one per file.</para>
///     <para>READ-ONLY (design D4): nothing here stages, commits, or checks anything out.
///     A non-git workspace renders an empty state naming why, never an error - the
///     absence of a repository is a fact about the workspace, not a failure.</para></summary>
internal sealed partial class GitViewViewModel : ObservableObject, IDisposable
{
  /// <summary>Coalescing delay between a filesystem change and the refresh. Short enough
  ///     that an edit in another editor lands well inside a quarter second, long enough
  ///     that one save's burst of writes is a single query.</summary>
  internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

  /// <summary>Directories under the workspace root that never trigger a refresh. A build
  ///     writes thousands of files under bin/obj; watching those turns a compile into a
  ///     storm of git invocations.
  ///     <para>Matched case-insensitively as whole path SEGMENTS, so 'obj' never excludes
  ///     a directory called 'objects'.</para></summary>
  internal static readonly IReadOnlyList<string> ExcludedDirectories = [".git", "bin", "obj", "node_modules"];

  /// <summary>Both separator forms, so a watcher path and a relative path are split the
  ///     same way on Windows and elsewhere.</summary>
  private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

  private readonly string _workspaceRoot;
  private readonly IGitQueryAccess? _git;
  private readonly Action<Action> _marshal;
  private readonly FileSystemWatcher? _watcher;
  private readonly CancellationTokenSource _cancelled = new();
  private readonly Lock _gate = new();
  private Task _refresh = Task.CompletedTask;
  private bool _started;
  private bool _disposed;

  public GitViewViewModel(string workspaceRoot, IGitQueryAccess? git, Action<Action> marshal)
  {
    ArgumentNullException.ThrowIfNull(workspaceRoot);
    _workspaceRoot = workspaceRoot;
    _git = git;
    _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
    _watcher = TryCreateWatcher(workspaceRoot);
  }

  /// <summary>The CHANGES list: untracked first (they are the ones a person forgets),
  ///     then unstaged, then staged - each group in git's own order.</summary>
  public ObservableCollection<GitChangeRowViewModel> Changes { get; } = [];

  /// <summary>The selected path's diff, one entry per line. Empty when nothing is
  ///     selected or the selection has no differences to show.</summary>
  public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];

  /// <summary>The base branch a pull request would target, derived from the tracking branch
  ///     ('origin/master' -> 'master'). Empty when the branch tracks nothing, in which case
  ///     the user names a base in the create form rather than being handed a guess.</summary>
  public string SuggestedBaseBranch { get; private set; } = "";

  /// <summary>The branch line: 'master' or 'master → origin/master' when a tracking
  ///     branch is configured.</summary>
  [ObservableProperty]
  public partial string BranchDisplay { get; private set; } = "";

  /// <summary>The dirty summary: 'clean', '2 untracked', '3 changed'.
  ///     A view that says nothing about dirtiness is indistinguishable from a clean tree.</summary>
  [ObservableProperty]
  public partial string DirtyDisplay { get; private set; } = "";

  /// <summary>True when the tree has no changes at all.</summary>
  [ObservableProperty]
  public partial bool IsClean { get; private set; }

  /// <summary>The HEAD line: the abbreviated hash and the commit subject. Empty for a
  ///     repository with no commits yet.</summary>
  [ObservableProperty]
  public partial string HeadDisplay { get; private set; } = "";

  /// <summary>The header's right-hand status: 'watched · workspace', or the reason the
  ///     view has nothing to show.
  ///     <para>It names the WATCH ROOT deliberately: the design's header said
  ///     'watched · .git', which is the wrong surface for a view whose whole point is
  ///     working-tree edits.</para></summary>
  [ObservableProperty]
  public partial string StatusLine { get; private set; } = "";

  /// <summary>Non-null when the workspace is not a git repository (or git could not be
  ///     asked). Renders INSTEAD of the status list - 'not a repo' and 'a clean repo'
  ///     must never look the same.</summary>
  [ObservableProperty]
  public partial string? EmptyReason { get; private set; }

  /// <summary>The selected path, or null when nothing is selected.</summary>
  [ObservableProperty]
  public partial string? SelectedPath { get; private set; }

  /// <summary>The DIFF heading: 'DIFF · &lt;path&gt;', or a hint when nothing is selected.</summary>
  [ObservableProperty]
  public partial string DiffHeading { get; private set; } = "DIFF";

  /// <summary>Non-null when the selected path's diff could not be read.</summary>
  [ObservableProperty]
  public partial string? DiffError { get; private set; }

  /// <summary>Starts the watch and runs the first read. Idempotent, and never starts for
  ///     a view nobody is looking at: a collapsed workbench costs nothing.
  ///     <para>The initial read runs even when the live watch could not start, so a
  ///     workspace on a network drive still shows its status once.</para></summary>
  public void Start()
  {
    if (_disposed || _started)
    {
      return;
    }

    _started = true;
    _watcher?.EnableRaisingEvents = true;
    RequestRefresh();
  }

  /// <summary>Selects a path and renders its diff. Selecting the same path again is a
  ///     no-op, so a refresh loop cannot re-read what it already shows.
  ///     <para>Read-only: the selection never mutates the repository.</para></summary>
  public async Task SelectAsync(string? path, CancellationToken ct)
  {
    if (string.Equals(SelectedPath, path, StringComparison.Ordinal))
    {
      return;
    }

    SelectedPath = path;
    DiffHeading = path is null ? "DIFF" : $"DIFF \u00B7 {path}";
    await LoadDiffAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Re-reads status and the selected diff and re-renders. Concurrent calls
  ///     collapse into one: a refresh already in flight would read the same state, and
  ///     the watcher will fire again for anything newer.</summary>
  public async Task RefreshAsync(CancellationToken ct)
  {
    if (_disposed)
    {
      return;
    }

    if (_git is null)
    {
      Apply(() =>
      {
        EmptyReason = "git is unavailable in this session.";
        StatusLine = "unavailable";
      });
      return;
    }

    Result<GitStatus> status = await _git.GetStatusAsync(_workspaceRoot, ct).ConfigureAwait(false);
    if (!status.IsSuccess)
    {
      Apply(() => RenderUnavailable(status.Error));
      return;
    }

    GitStatus snapshot = status.Value;
    Apply(() => RenderStatus(snapshot));
    await LoadDiffAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Renders the status snapshot. Kept separate from the read so the whole
  ///     render happens in ONE marshalled block - a partially applied snapshot is a
  ///     view that disagrees with itself.
  ///     <para>Declared after the collections it fills, so no observable property is
  ///     assigned before the collection it describes.</para></summary>
  private void RenderStatus(GitStatus status)
  {
    BranchDisplay = status.Upstream is { Length: > 0 } upstream
        ? $"{status.Branch} \u2192 {upstream}"
        : status.Branch;
    SuggestedBaseBranch = BaseBranchFrom(status.Upstream);
    HeadDisplay = status.HeadCommit is { } head
        ? $"{head.Hash} {head.Subject}".TrimEnd()
        : "(no commits yet)";

    int changed = status.Staged.Count + status.Unstaged.Count;
    int untracked = status.Untracked.Count;
    DirtyDisplay = DescribeDirty(changed, untracked);
    IsClean = changed == 0 && untracked == 0;
    EmptyReason = null;
    StatusLine = "watched \u00B7 workspace";

    Changes.Clear();
    foreach (string path in status.Untracked)
    {
      Changes.Add(GitChangeRowViewModel.Untracked(path));
    }

    foreach (GitStatusEntry entry in status.Unstaged)
    {
      Changes.Add(GitChangeRowViewModel.For(entry, staged: false));
    }

    foreach (GitStatusEntry entry in status.Staged)
    {
      Changes.Add(GitChangeRowViewModel.For(entry, staged: true));
    }
  }

  /// <summary>Strips the remote name from a tracking branch, so a PR's base is the branch
  ///     name the provider knows ('origin/master' -> 'master'). A remote-less or detached
  ///     entry yields empty rather than a fabricated name.</summary>
  internal static string BaseBranchFrom(string? upstream)
  {
    if (string.IsNullOrWhiteSpace(upstream))
    {
      return string.Empty;
    }

    string trimmed = upstream.Trim();
    int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
    return slash < 0 ? trimmed : trimmed[(slash + 1)..];
  }

  /// <summary>The dirty summary for a change count and an untracked count. Both counts
  ///     are reported when both are non-zero: '2 changed' alone would hide untracked
  ///     work, which is usually the thing a person has forgotten about.</summary>
  private static string DescribeDirty(int changed, int untracked) => (changed, untracked) switch
  {
    (0, 0) => "clean",
    (0, _) => string.Create(CultureInfo.InvariantCulture, $"{untracked} untracked"),
    (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{changed} changed"),
    _ => string.Create(CultureInfo.InvariantCulture, $"{changed} changed, {untracked} untracked"),
  };

  /// <summary>Renders a workspace that cannot be read as a git repository. The reason is
  ///     NAMED: 'not a git repository' is an empty state a person can act on, while a
  ///     blank pane is indistinguishable from a broken view.</summary>
  private void RenderUnavailable(DomainError error)
  {
    SuggestedBaseBranch = string.Empty;
    Changes.Clear();
    DiffLines.Clear();
    SelectedPath = null;
    DiffHeading = "DIFF";
    DiffError = null;
    BranchDisplay = "";
    DirtyDisplay = "";
    HeadDisplay = "";
    IsClean = false;
    EmptyReason = error.Code == "NotAGitRepository"
        ? "This workspace is not a git repository."
        : $"git status unavailable: {error.Message}";
    StatusLine = error.Code == "NotAGitRepository" ? "no repository" : "error";
  }

  private async Task LoadDiffAsync(CancellationToken ct)
  {
    string? path = SelectedPath;
    if (_git is null || path is null)
    {
      Apply(() =>
      {
        DiffLines.Clear();
        DiffError = null;
      });
      return;
    }

    // 'All' rather than 'Unstaged': a path can be dirty in the index, in the worktree,
    // or both, and the view answers 'what changed in this file', not 'what changed in
    // this file that is not staged'.
    Result<GitDiff> diff = await _git.GetDiffAsync(_workspaceRoot, "All", path, ct).ConfigureAwait(false);
    if (!diff.IsSuccess)
    {
      Apply(() =>
      {
        DiffLines.Clear();
        DiffError = $"diff unavailable: {diff.Error.Message}";
      });
      return;
    }

    List<DiffLineViewModel> rendered = [.. SplitLines(diff.Value.Patch).Select(DiffLineViewModel.For)];
    Apply(() =>
    {
      DiffLines.Clear();
      foreach (DiffLineViewModel line in rendered)
      {
        DiffLines.Add(line);
      }

      DiffError = null;
    });
  }

  /// <summary>Splits a patch into lines, dropping the empty entries a trailing newline
  ///     produces. A diff line is never legitimately empty: an empty CONTEXT line is a
  ///     single space, and an empty added line is a single '+'.</summary>
  private static string[] SplitLines(string patch)
      => patch.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

  /// <summary>Applies a render on the UI thread. Every mutation of the observable
  ///     collections goes through here: refreshes complete on pool threads.</summary>
  private void Apply(Action render)
  {
    if (_disposed)
    {
      return;
    }

    _marshal(render);
  }

  /// <summary>Schedules one debounced refresh; overlapping schedules coalesce, so a
  ///     burst of writes costs one git query.</summary>
  private void RequestRefresh()
  {
    lock (_gate)
    {
      if (!_refresh.IsCompleted)
      {
        return;
      }

      _refresh = DebouncedRefreshAsync(_cancelled.Token);
    }
  }

  private async Task DebouncedRefreshAsync(CancellationToken ct)
  {
    try
    {
      await Task.Delay(Debounce, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return;
    }

    await RefreshAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Creates the watch over the workspace root, or null when the root cannot be
  ///     watched. Named decision (CA1031): a workspace on a network drive or one the
  ///     process cannot watch degrades to the one-shot read <see cref="Start"/> still
  ///     performs, instead of crashing the tab open.</summary>
  private FileSystemWatcher? TryCreateWatcher(string workspaceRoot)
  {
    if (!Directory.Exists(workspaceRoot))
    {
      return null;
    }

    FileSystemWatcher watcher = new()
    {
      Path = workspaceRoot,
      NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
      IncludeSubdirectories = true,
      InternalBufferSize = 32 * 1024,
    };
    watcher.Changed += OnChanged;
    watcher.Created += OnChanged;
    watcher.Deleted += OnChanged;
    watcher.Renamed += OnRenamed;
    watcher.Error += OnWatcherError;
    return watcher;
  }

  private void OnChanged(object sender, FileSystemEventArgs e)
  {
    if (!IsExcluded(e.FullPath))
    {
      RequestRefresh();
    }
  }

  private void OnRenamed(object sender, RenamedEventArgs e)
  {
    if (!IsExcluded(e.FullPath) || !IsExcluded(e.OldFullPath))
    {
      RequestRefresh();
    }
  }

  /// <summary>A watcher whose buffer overflowed has MISSED events, so the view is stale
  ///     with no way to know how. One refresh resynchronises it; the watcher itself
  ///     keeps running (a rebuild is exactly when this fires).</summary>
  private void OnWatcherError(object sender, ErrorEventArgs e) => RequestRefresh();

  /// <summary>True when any path SEGMENT below the workspace root is an excluded
  ///     directory name. Segment matching is what keeps 'obj' from excluding a folder
  ///     called 'objects' - a prefix comparison would silently hide real work.</summary>
  internal bool IsExcluded(string fullPath)
  {
    if (string.IsNullOrEmpty(fullPath))
    {
      return false;
    }

    string relative = Path.GetRelativePath(_workspaceRoot, fullPath);
    return relative.StartsWith("..", StringComparison.Ordinal)
        || relative
            .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _cancelled.Cancel();
    _watcher?.Dispose();
    _cancelled.Dispose();
  }
}
