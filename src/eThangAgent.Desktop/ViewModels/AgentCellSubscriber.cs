using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turns an agent's lifecycle facts into grid cells (issue #329): a
///     <see cref="ChildStartedEvent"/> appends a cell to the tab for the agent's OWN
///     workspace, and a <see cref="ChildSettledEvent"/> marks that cell finished.
///     <para>CREATED FROM THE EVENT, NEVER FROM A QUERY. Polling
///     <c>IAgentStore.ListChildrenAsync</c> is the mechanism this replaces: a scan cannot tell
///     'just started' from 'was already there', so it would resurrect every agent a workspace
///     has ever run - and re-append them on every tick. The started fact names the agent the
///     instant it begins, which is exactly the moment a cell should exist.</para>
///     <para>ONE CELL PER AGENT, ENFORCED HERE AND IN THE TAB. A second started event for the
///     same id is a watchdog retry of the SAME agent, not a new one: it reactivates the
///     existing cell instead of appending a duplicate.</para>
///     <para>THE CELL'S LABEL AND ITS CHAIN COME FROM THE AGENT'S OWN RECORD, read once at
///     start. The event carries metadata only (ids, timestamps, model, attempts) by the
///     ChildEvent family's own contract, so neither the label a user typed nor the chain step
///     the agent executes is on it - and D9 requires the UI to show a label. One read at
///     creation is a creation-time fact, not the per-tick polling this design rejects.</para>
///     <para>THREADING: events arrive on stream threads; every cell mutation runs through the
///     caller's marshal, which the shell binds to the UI dispatcher. The store read and the
///     view-model build are async, so the cell is appended once they complete - still without
///     blocking the stream.</para></summary>
/// <param name="tabForWorkspace">Resolves the tab for a WORKSPACE ID. An agent carries its own
///     workspace and has no parent, so this is the whole of 'where does its cell belong'</param>
/// <param name="openTabs">Every open tab, so a settled agent is found wherever it is shown: a
///     settled fact names no workspace, and the cell may sit in any tab.</param>
/// <param name="storeFor">Resolves the shared store the agent's record and transcript live in.
///     Every tab's store addresses the same database, so any live session's store serves.</param>
/// <param name="chainStoreFor">Resolves the chain store of the tab's workspace, which is where
///     the step an agent executes is recorded. Null when that container wired no chains, which
///     is then simply 'this agent states no placement'.</param>
/// <param name="cellViewModelFor">Builds the view-model a host agent's cell renders.</param>
/// <param name="marshal">Runs a cell mutation on the UI thread.</param>
internal sealed class AgentCellSubscriber(
    Func<string, AgentTabViewModel?> tabForWorkspace,
    Func<IReadOnlyList<AgentTabViewModel>> openTabs,
    Func<IAgentStore?> storeFor,
    Func<AgentTabViewModel, IChainStore?> chainStoreFor,
    Func<AgentTabViewModel, AgentId, string, ChainPlacement?, IAgentStore?, AgentSessionViewModel> cellViewModelFor,
    Action<Action> marshal) : IAgentEventSubscriber
{
  private readonly Func<string, AgentTabViewModel?> _tabForWorkspace =
      tabForWorkspace ?? throw new ArgumentNullException(nameof(tabForWorkspace));
  private readonly Func<IReadOnlyList<AgentTabViewModel>> _openTabs =
      openTabs ?? throw new ArgumentNullException(nameof(openTabs));
  private readonly Func<IAgentStore?> _storeFor =
      storeFor ?? throw new ArgumentNullException(nameof(storeFor));
  private readonly Func<AgentTabViewModel, IChainStore?> _chainStoreFor =
      chainStoreFor ?? throw new ArgumentNullException(nameof(chainStoreFor));
  private readonly Func<AgentTabViewModel, AgentId, string, ChainPlacement?, IAgentStore?, AgentSessionViewModel> _cellViewModelFor =
      cellViewModelFor ?? throw new ArgumentNullException(nameof(cellViewModelFor));
  private readonly Action<Action> _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));

  /// <summary>Routes one agent fact to its cell. Every other kind is ignored: progress beats
  ///     drive a live phase chip (#331), which is a later issue's surface, and the
  ///     durable-record facts belong to the rail.
  ///     <para>Contained: a presentation fault must never reach the agent loop, the same rule
  ///     every stream subscriber follows.</para></summary>
  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
#pragma warning disable CA1031 // Named decision: a UI fault must never reach the agent loop.
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

  /// <summary>Appends the cell for a started agent: reads the agent's own record for its
  ///     workspace, label, and chain step, resolves the tab from the workspace, builds the
  ///     view-model, and appends - all off the stream thread except the append itself.
  ///     <para>THE WORKSPACE IS THE AGENT'S OWN, never a parent's: an agent has no parent
  ///     (issue #299 D1), and a tab is a workspace (D10), so the record's own workspace stamp
  ///     is the whole of the answer.</para>
  ///     <para>AN AGENT WHOSE WORKSPACE HAS NO OPEN TAB IS DROPPED, not queued: it keeps
  ///     running in its host, but a cell can only exist inside an open tab, and inventing one
  ///     would open a workspace the user did not ask for.</para></summary>
  private async Task StartAsync(ChildStartedEvent started)
  {
    // The fact family names the field ChildId; in this path it is the agent's id, so it is named
    // that from here on and nothing downstream reads it as anything else.
    AgentId agentId = started.ChildId;
    IAgentStore? store = _storeFor();
    AgentRecord? record = await ReadRecordAsync(store, agentId).ConfigureAwait(false);
    if (record?.WorkspaceId is not { } workspaceId || _tabForWorkspace(workspaceId) is not { } tab)
    {
      return;
    }

    if (tab.FindCell(agentId) is { } existing)
    {
      // A retried run of an agent this tab already shows: the cell is the same cell.
      _marshal(existing.StartFollowing);
      return;
    }

    string label = record.Label ?? string.Empty;
    ChainPlacement? placement = await ReadPlacementAsync(_chainStoreFor(tab), agentId)
        .ConfigureAwait(false);
    _marshal(() =>
    {
      // RE-RESOLVED INSIDE THE MARSHAL: the tab may have closed while the record was read,
      // and appending to a tab the shell no longer holds would leak a cell nobody renders.
      if (_tabForWorkspace(workspaceId) is not { } live)
      {
        return;
      }

      _ = live.AddAgentCell(agentId, label, placement, store,
          _cellViewModelFor(live, agentId, label, placement, store));
    });
  }

  /// <summary>Marks the settled agent's cell finished, so the card dims and its cross appears
  ///     (D6) - the agent is done and its report is what remains.
  ///     <para>A settled agent with no cell is ordinary, not a fault: it can settle before the
  ///     record read that creates its cell completes, or after its tab was closed.</para></summary>
  private void Settle(ChildSettledEvent settled)
  {
    AgentId agentId = settled.ChildId;
    foreach (AgentTabViewModel tab in _openTabs())
    {
      if (tab.FindCell(agentId) is { } cell)
      {
        cell.MarkFinished();
        return;
      }
    }
  }

  /// <summary>Reads the agent's own record. A miss yields null, which drops the cell: without
  ///     a workspace there is nowhere to put it, and without a record there is no label either.
  ///     </summary>
  private static async Task<AgentRecord?> ReadRecordAsync(IAgentStore? store, AgentId agentId)
  {
    if (store is null)
    {
      return null;
    }

    Result<AgentRecord> record = await store.GetAsync(agentId).ConfigureAwait(false);
    return record.IsSuccess ? record.Value : null;
  }

  /// <summary>Reads the chain step the agent executes, from the chain record's own
  ///     <c>session_id</c> link (issue #329). A store that cannot answer, or an agent that runs
  ///     no step, yields null - which is what the cell renders as 'nothing extra'.
  ///     <para>ONE QUERY, INDEXED BY SESSION: the link is on the step row, so the store answers
  ///     'which chain claimed this session' directly rather than the caller scanning a
  ///     workspace's chains.</para>
  ///     <para>BEST EFFORT: a chain that cannot be read must not cost the agent its cell. The
  ///     placement is a caption, and a caption is never worth failing a cell over.</para></summary>
  private static async Task<ChainPlacement?> ReadPlacementAsync(
      IChainStore? chains, AgentId agentId)
  {
    if (chains is null)
    {
      return null;
    }

    string sessionId = agentId.ToString();
    Result<Chain?> found = await chains.FindBySessionAsync(sessionId).ConfigureAwait(false);
    if (!found.IsSuccess || found.Value is not { } chain)
    {
      return null;
    }

    ChainStep? step = chain.Steps.FirstOrDefault(
        candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal));
    return step is null
        ? null
        : new ChainPlacement(chain.Id, chain.Title, step.Position, chain.Steps.Count);
  }
}
