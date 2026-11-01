using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turns a spawned agent's lifecycle facts into grid cells (issue #329): a
///     <see cref="ChildStartedEvent"/> appends a cell to the workspace tab it belongs in, and
///     a <see cref="ChildSettledEvent"/> marks that cell finished.
///     <para>CREATED FROM THE EVENT, NEVER FROM A QUERY. The parent issue is explicit that
///     polling <c>IAgentStore.ListChildrenAsync</c> is the mechanism this replaces: a scan
///     cannot tell 'just spawned' from 'was already there', so it would resurrect every child
///     a workspace has ever run - and re-append them on every tick. The started fact names the
///     agent the instant it begins, which is exactly the moment a cell should exist.</para>
///     <para>ONE CELL PER AGENT, ENFORCED HERE AND IN THE TAB. A second started event for the
///     same id is a watchdog retry of the SAME agent, not a new one: it reactivates the
///     existing cell instead of appending a duplicate, so a retried run cannot fill the grid
///     with copies of one agent.</para>
///     <para>THE CELL'S LABEL COMES FROM THE CHILD'S RECORD, read once at start. The event
///     carries metadata only (ids, timestamps, model, attempts) by the ChildEvent family's own
///     contract, so the spawn label a user typed is not on it - and D9 requires the UI to show
///     a label. One read at creation is a creation-time fact, not the per-tick polling this
///     design rejects.</para>
///     <para>THREADING: events arrive on stream threads; every cell mutation runs through the
///     caller's marshal, which the shell binds to the UI dispatcher. The store read and the
///     view-model build are async, so the cell is appended once they complete - still without
///     blocking the stream.</para></summary>
/// <param name="tabFor">Resolves the tab a spawned agent belongs in, by the parent session
///     that spawned it (D10: a tab is per workspace, holding that workspace's agents). Null
///     when no open tab owns the spawner.</param>
/// <param name="openTabs">Every open tab, so a settled agent is found wherever it is shown:
///     a settled fact names no parent, and the cell may sit in any workspace's tab.</param>
/// <param name="storeFor">Resolves the shared store the child's transcript lands in, from the
///     spawning session.</param>
/// <param name="cellViewModelFor">Builds the view-model a spawned cell renders.</param>
/// <param name="marshal">Runs a cell mutation on the UI thread.</param>
internal sealed class SpawnedCellSubscriber(
    Func<AgentId, AgentTabViewModel?> tabFor,
    Func<IReadOnlyList<AgentTabViewModel>> openTabs,
    Func<AgentId, IAgentStore?> storeFor,
    Func<AgentTabViewModel, AgentId, string, IAgentStore?, AgentSessionViewModel> cellViewModelFor,
    Action<Action> marshal) : IAgentEventSubscriber
{
  private readonly Func<AgentId, AgentTabViewModel?> _tabFor =
      tabFor ?? throw new ArgumentNullException(nameof(tabFor));
  private readonly Func<IReadOnlyList<AgentTabViewModel>> _openTabs =
      openTabs ?? throw new ArgumentNullException(nameof(openTabs));
  private readonly Func<AgentId, IAgentStore?> _storeFor =
      storeFor ?? throw new ArgumentNullException(nameof(storeFor));
  private readonly Func<AgentTabViewModel, AgentId, string, IAgentStore?, AgentSessionViewModel> _cellViewModelFor =
      cellViewModelFor ?? throw new ArgumentNullException(nameof(cellViewModelFor));
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));

  /// <summary>Routes one child event to its cell. Every other kind is ignored: progress
  ///     beats drive a live phase chip (#331), which is a later issue's surface, and the
  ///     durable-record facts belong to the rail.
  ///     <para>Contained: a presentation fault must never reach the child loop, the same rule
  ///     every stream subscriber follows.</para></summary>
  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
#pragma warning disable CA1031 // Named decision: a UI fault must never reach the child loop.
    try
    {
      switch (evt)
      {
        case ChildStartedEvent started:
          _ = StartAsync(started);
          break;
        case ChildSettledEvent settled:
          _marshal(() => Settle(settled));
          break;
        default:
          break;
      }
    }
    catch
    {
      // Swallowed deliberately: see the named decision above.
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }

  /// <summary>Appends the cell for a started agent: resolves the tab, reads the child's
  ///     record for its label, builds the view-model, and appends - all off the stream thread
  ///     except the append itself.
  ///     <para>A spawner with no open tab is DROPPED, not queued: the agent keeps running in
  ///     its host, but a cell can only exist inside an open tab, and inventing one would open
  ///     a workspace the user did not ask for.</para></summary>
  private async Task StartAsync(ChildStartedEvent started)
  {
    if (started.ParentId is not { } parent || _tabFor(parent) is not { } tab)
    {
      return;
    }

    if (tab.FindCell(started.ChildId) is { } existing)
    {
      // A retried run of an agent this tab already shows: the cell is the same cell.
      _marshal(existing.StartFollowing);
      return;
    }

    IAgentStore? store = _storeFor(parent);
    string label = await ReadLabelAsync(store, started.ChildId).ConfigureAwait(false);
    _marshal(() =>
    {
      // RE-RESOLVED INSIDE THE MARSHAL: the tab may have closed while the record was read,
      // and appending to a tab the shell no longer holds would leak a cell nobody renders.
      if (_tabFor(parent) is not { } live)
      {
        return;
      }

      _ = live.AddSpawnedCell(started.ChildId, label, store,
          _cellViewModelFor(live, started.ChildId, label, store));
    });
  }

  /// <summary>Marks the settled agent's cell finished, so the card dims and its ✕ appears
  ///     (D6) - the agent is done and its report is what remains.
  ///     <para>A settled agent with no cell is ordinary, not a fault: it can settle before the
  ///     store read that creates its cell completes, or after its tab was closed.</para></summary>
  private void Settle(ChildSettledEvent settled)
  {
    foreach (AgentTabViewModel tab in _openTabs())
    {
      if (tab.FindCell(settled.ChildId) is { } cell)
      {
        cell.MarkFinished();
        return;
      }
    }
  }

  /// <summary>Reads the child's record for its spawn label. A miss yields the empty label,
  ///     and the cell falls back to the short id - a caption is never worth failing a cell
  ///     over.</summary>
  private static async Task<string> ReadLabelAsync(IAgentStore? store, AgentId childId)
  {
    if (store is null)
    {
      return string.Empty;
    }

    Result<AgentRecord> record = await store.GetAsync(childId).ConfigureAwait(false);
    return record.IsSuccess ? record.Value.Label ?? string.Empty : string.Empty;
  }
}
