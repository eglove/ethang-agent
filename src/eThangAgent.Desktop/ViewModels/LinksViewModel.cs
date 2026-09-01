using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One row of the Links dialog's candidate list: a persisted agent (root
///     session or spawned child) the user may link into this session. Carries display
///     fields only — the link itself stores the agent's id address.</summary>
internal sealed record CandidateRow(
    AgentId Id,
    string Label,
    string WorkspaceName,
    string ProviderDisplay,
    string StatusDisplay,
    string CreatedDisplay,
    string WorkspaceRoot)
{
  /// <summary>The secondary line: provider, status, and workspace.</summary>
  public string Detail => $"{ProviderDisplay} · {StatusDisplay} · {WorkspaceName}";

  public string ToolTip => $"{Id} · {WorkspaceRoot}";
}

/// <summary>One row of the Links dialog's live-links list: a consented link currently
///     held by this session's registry. The target renders as the short id — the
///     full address stays visible in the tooltip.</summary>
internal sealed record LinkRow(string Name, string Container, string AgentAddress)
{
  public string TargetDisplay => AgentAddress.Length > 8 ? AgentAddress[..8] : AgentAddress;

  public string Detail => $"→ {TargetDisplay} · {Container}";
}

/// <summary>View-model behind the Links dialog: the agents that can be linked (from
///     the session's store) and the links already consented (from the session's
///     in-memory registry). Link creation is THE consent door (design D10, handoff
///     item 1): the user names the link and picks the target agent here, and the
///     command registers the link with consented: true on the REAL registry — no other
///     production caller exists. Revocation mirrors it. Pure state and commands;
///     window mechanics belong to the view. The candidate load runs off the UI
///     thread. Guards fail with structured error text, never exceptions — the dialog
///     stays open and the user can correct the input.</summary>
internal sealed partial class LinksViewModel : ObservableObject
{
  private readonly Func<CancellationToken, Task<Result<IReadOnlyList<AgentRecord>>>> _load;
  private readonly AgentLinkRegistry _registry;
  private bool _loaded;

  public IRelayCommand LinkCommand { get; }

  public IRelayCommand RevokeCommand { get; }

  [ObservableProperty]
  public partial bool IsLoading { get; set; }

  [ObservableProperty]
  public partial string? LoadError { get; set; }

  [ObservableProperty]
  public partial string? LinkError { get; set; }

  [ObservableProperty]
  public partial IReadOnlyList<CandidateRow> Candidates { get; set; } = [];

  [ObservableProperty]
  public partial IReadOnlyList<LinkRow> Links { get; set; } = [];

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
  public partial CandidateRow? SelectedCandidate { get; set; }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(RevokeCommand))]
  public partial LinkRow? SelectedLink { get; set; }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(LinkCommand))]
  public partial string LinkName { get; set; } = string.Empty;

  /// <param name="load">Lists the persisted agents (UI thread escape handled here).</param>
  /// <param name="registry">The session's link registry — the REAL consent object.
  ///     Every link this dialog creates is a consented production link.</param>
  public LinksViewModel(
      Func<CancellationToken, Task<Result<IReadOnlyList<AgentRecord>>>> load,
      AgentLinkRegistry registry)
  {
    _load = load ?? throw new ArgumentNullException(nameof(load));
    _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    // The command exists before the observable properties: setting those raises the
    // changed hooks, which requery command availability. Guards in the actions are
    // load-bearing: ICommand.Execute does not consult CanExecute.
    LinkCommand = new RelayCommand(Link, () => !string.IsNullOrWhiteSpace(LinkName));
    RevokeCommand = new RelayCommand(Revoke, () => SelectedLink is not null);
  }

  /// <summary>Fetches the persisted agents off the UI thread and fills both lists.
  ///     A failure lands in <see cref="LoadError"/> (the dialog stays open; the user
  ///     can cancel). Only the first call loads.</summary>
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
    // RefreshLinks is lock-guarded inside the registry; a fault there is a bug, but a
    // dialog must not crash the shell over one.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Task<Result<IReadOnlyList<AgentRecord>>> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() => _load(CancellationToken.None));
      }

      Result<IReadOnlyList<AgentRecord>> records = await scheduled.ConfigureAwait(true);
      if (!records.IsSuccess)
      {
        LoadError = records.Error.Message;
        return;
      }

      Candidates = [.. records.Value
          .OrderByDescending(record => record.CreatedAt)
          .Select(record => new CandidateRow(
              record.Id,
              string.IsNullOrWhiteSpace(record.Label) ? "(unlabeled)" : record.Label,
              WorkspaceName(record.WorkspaceId),
              record.Provider is null ? "unbound" : Providers.DisplayName(record.Provider),
              record.Status.ToString(),
              record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
              record.WorkspaceId ?? string.Empty))];
      SelectedCandidate = null;
      RefreshLinks();
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

  /// <summary>THE consent door (D10): registers a consented link on the session's
  ///     registry, named by the user, addressed to the selected agent. Input problems
  ///     and registry failures land in <see cref="LinkError"/>; success clears the
  ///     error, resets the name field, and refreshes the live-links list.</summary>
  private void Link()
  {
    string name = LinkName.Trim();
    if (name.Length == 0)
    {
      LinkError = "A link name is required — the agent addresses the link by it.";
      return;
    }

    if (SelectedCandidate is not { } target)
    {
      LinkError = "Select the agent this link addresses.";
      return;
    }

    Result<LinkAddress> linked = _registry.Link(
        name, target.WorkspaceRoot, target.Id.Value.ToString("D"), consented: true);
    if (!linked.IsSuccess)
    {
      LinkError = linked.Error.Message;
      return;
    }

    LinkError = null;
    LinkName = string.Empty;
    RefreshLinks();
  }

  /// <summary>Revokes the selected link. An already-gone link is information
  ///     (A3), not silence — but the list refresh is the visible outcome either way.</summary>
  private void Revoke()
  {
    if (SelectedLink is not { } link)
    {
      return;
    }

    _ = _registry.Revoke(link.Name);
    SelectedLink = null;
    RefreshLinks();
  }

  /// <summary>Re-reads the registry's live links (newest first, per its snapshot
  ///     contract) into the dialog's list.</summary>
  private void RefreshLinks()
    => Links = [.. _registry.Snapshot.Select(address => new LinkRow(
        address.Name, address.Container, address.AgentAddress))];

  private static string WorkspaceName(string? workspaceRoot) => workspaceRoot is null
      ? "(unbound)"
      : Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
