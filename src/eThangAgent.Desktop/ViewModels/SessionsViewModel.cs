using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One row of the Sessions dialog: a persisted root session with its workspace
///     and provider binding, the time it started, and whether a tab already has it open.
///     An open row stays selectable for inspection but greyed (dimmed) and cannot be
///     resumed — the hover tooltip explains why.</summary>
internal sealed record SessionRow(
    AgentId Id,
    string WorkspaceName,
    string WorkspaceRoot,
    string ProviderDisplay,
    string CreatedDisplay,
    string StatusDisplay,
    bool IsOpen)
{
  public double Dimming => IsOpen ? 0.45 : 1.0;

  /// <summary>The secondary line: provider, start time, and lifecycle status.</summary>
  public string Detail => $"{ProviderDisplay} · started {CreatedDisplay} · {StatusDisplay}";

  public string ToolTip => IsOpen
      ? "This session is already open in a tab — select that tab to continue it."
      : WorkspaceRoot;
}

/// <summary>View-model behind the Sessions dialog: every resumable root session, newest
///     first, with already-open ones greyed. Pure state and commands; window closing and
///     applying the resume belong to the caller. The catalog load runs off the UI thread
///     and populates the rows when it lands. Confirming raises the session id; an
///     already-open row never confirms — resume selects the open tab instead of
///     double-resuming.</summary>
internal sealed partial class SessionsViewModel : ObservableObject
{
  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<SessionCatalogEntry>>>> _load;
  private readonly Func<IReadOnlySet<AgentId>> _openSessionIds;
  private bool _loaded;

  /// <summary>Raised when the user confirms a resumable row; carries the session id.
  ///     The view closes the dialog with it.</summary>
  public event EventHandler<AgentId>? ConfirmRequested;

  public IRelayCommand ConfirmCommand { get; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<SessionRow> Rows { get; set; } = [];

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
  public partial SessionRow? SelectedRow { get; set; }

  /// <param name="load">Lists the session catalog (UI thread escape handled here).</param>
  /// <param name="openSessionIds">Live ids of the tabs currently open — queried at load
  ///     time so grey-out reflects this shell's state, not a stale snapshot.</param>
  public SessionsViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<SessionCatalogEntry>>>> load,
      Func<IReadOnlySet<AgentId>> openSessionIds)
  {
    _load = load ?? throw new ArgumentNullException(nameof(load));
    _openSessionIds = openSessionIds ?? throw new ArgumentNullException(nameof(openSessionIds));

    // The command exists before the observable properties: setting those raises the
    // changed hooks, which requery command availability. The guard in the action is
    // load-bearing: ICommand.Execute does not consult CanExecute, and a disabled
    // button is only one of several ways this command can be invoked.
    ConfirmCommand = new RelayCommand(Confirm, () => SelectedRow is { IsOpen: false });
  }

  /// <summary>Fetches the catalog off the UI thread and fills the rows, greyed for the
  ///     sessions already open in a tab. A failure lands in <see cref="LoadError"/> (the
  ///     dialog stays open; the user can cancel). Only the first call loads.</summary>
  public async Task LoadAsync()
  {
    if (IsLoading || _loaded)
    {
      return;
    }

    _loaded = true;
    IsLoading = true;
    // Named decision (CA1031): a loader fault must land in the dialog's error state,
    // never escape — the fire-and-forget caller cannot observe it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      IReadOnlySet<AgentId> open = _openSessionIds();
      Task<Result<IReadOnlyList<SessionCatalogEntry>>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _load(CancellationToken.None));
      }

      Result<IReadOnlyList<SessionCatalogEntry>> catalog = await scheduled;
      if (!catalog.IsSuccess)
      {
        LoadError = catalog.Error.Message;
        return;
      }

      Rows = [.. catalog.Value.Select(summary => new SessionRow(
          summary.Id,
          WorkspaceName(summary.WorkspaceId),
          summary.WorkspaceId,
          Providers.DisplayName(summary.Provider),
          summary.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
          summary.Status.ToString(),
          open.Contains(summary.Id)))];
      SelectedRow = Rows.FirstOrDefault(row => !row.IsOpen);
    }
    catch (Exception ex)
    {
      LoadError = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
#pragma warning restore CA1031
  }

  private void Confirm()
  {
    if (SelectedRow is { IsOpen: false } row)
    {
      ConfirmRequested?.Invoke(this, row.Id);
    }
  }

  private static string WorkspaceName(string workspaceRoot) => Path.GetFileName(
      workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
