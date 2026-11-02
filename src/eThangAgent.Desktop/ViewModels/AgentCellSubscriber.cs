using System.Globalization;
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
///     blocking the stream.</para>
///     <para>A FACT THAT ARRIVES WHILE ITS CELL IS STILL BEING CREATED IS HELD, NOT DROPPED.
///     Cell creation costs a store read and a chain read; a beat costs one marshal. The spawner
///     publishes the started fact and the agent loop's first beats back to back, so a beat
///     arriving before its cell exists is the ordinary path, and a subscriber that looked the
///     cell up and found nothing would silently lose the agent's whole first phase.</para></summary>
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

  /// <summary>Guards <see cref="_held"/>: a fact is held on the stream thread and released on
  ///     the UI thread, so the handoff needs one.</summary>
  private readonly Lock _heldGate = new();

  /// <summary>The facts that arrived for an agent whose cell is still being created, keyed by
  ///     agent and kept in arrival order. An entry exists from the started fact until its cell
  ///     lands, and every fact for that agent joins the list instead of being routed.</summary>
  private readonly Dictionary<AgentId, List<ChildEvent>> _held = [];

  /// <summary>Routes one agent fact to its cell: started appends it, settled finishes it,
  ///     a progress beat moves its phase chip (#331), and an idle or budget alert sets its
  ///     outline and states why in its own transcript. Every other kind is ignored - the
  ///     durable-record facts belong to the rail.
  ///     <para>EVERY CELL MUTATION RUNS THROUGH THE SAME MARSHAL, including the beats: they
  ///     arrive on stream threads and the chip is bound by the view, so a beat applied inline
  ///     would be a cross-thread property change. Routing them through the marshal is also what
  ///     keeps them IN ORDER relative to a settle that follows, because a single marshal queue
  ///     is FIFO.
  ///     </para>
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
          // HELD BEFORE THE FIRST AWAIT: from here until the cell lands, every fact for this
          // agent is kept rather than looked up, which is the whole of what closes the gap.
          Hold(started.ChildId);
          _ = StartAsync(started);
          break;
        case ChildSettledEvent settled:
          Route(settled.ChildId, settled);
          break;
        case ChildProgressEvent progress:
          Route(progress.ChildId, progress);
          break;
        case ChildIdleAlertEvent idle:
          Route(idle.ChildId, idle);
          break;
        case ChildBudgetAlertEvent budget:
          Route(budget.ChildId, budget);
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
      // No record or no open tab means no cell will ever exist for this agent, so the hold
      // opened by the started fact is closed here rather than left to grow.
      Forget(agentId);
      return;
    }

    if (tab.FindCell(agentId) is { } existing)
    {
      // A retried run of an agent this tab already shows: the cell is the same cell.
      _marshal(() =>
      {
        existing.StartFollowing();
        Release(agentId, existing);
      });
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
        // No cell is coming, so the held facts have nowhere to land: dropping the hold keeps
        // them from accumulating for an agent nothing renders.
        Forget(agentId);
        return;
      }

      AgentCellViewModel cell = live.AddAgentCell(agentId, label, placement, store,
          _cellViewModelFor(live, agentId, label, placement, store));
      Release(agentId, cell);
    });
  }

  /// <summary>Opens the hold for an agent whose cell is about to be created.
  ///     <para>AN EXISTING HOLD IS KEPT, not replaced: a second started fact for the same agent
  ///     is a watchdog retry of the SAME agent, and the facts already held for it are still the
  ///     facts its cell must see.</para></summary>
  /// <param name="agentId">The agent the started fact names.</param>
  private void Hold(AgentId agentId)
  {
    lock (_heldGate)
    {
      _ = _held.TryAdd(agentId, []);
    }
  }

  /// <summary>Drops an agent's hold and everything in it: no cell is coming, so there is
  ///     nothing to apply them to.</summary>
  /// <param name="agentId">The agent whose hold to drop.</param>
  private void Forget(AgentId agentId)
  {
    lock (_heldGate)
    {
      _ = _held.Remove(agentId);
    }
  }

  /// <summary>Applies every fact held for an agent to its cell, IN ARRIVAL ORDER, then closes
  ///     the hold so later facts route directly.
  ///     <para>RUNS INSIDE THE MARSHAL, so a held fact and a fact that arrives afterwards are
  ///     applied on one queue: the release cannot interleave with a beat that is already
  ///     posted, which is what makes the order a fact rather than a hope.</para></summary>
  /// <param name="agentId">The agent whose cell just landed.</param>
  /// <param name="cell">The cell to apply the held facts to.</param>
  private void Release(AgentId agentId, AgentCellViewModel cell)
  {
    List<ChildEvent>? held;
    lock (_heldGate)
    {
      if (!_held.TryGetValue(agentId, out held))
      {
        return;
      }

      _ = _held.Remove(agentId);
    }

    foreach (ChildEvent fact in held)
    {
      Apply(fact, cell);
    }
  }

  /// <summary>Routes a fact to its agent's cell, or holds it while that agent's cell is still
  ///     being created.
  ///     <para>THE LOOKUP RUNS INSIDE THE MARSHAL, as it always has: a fact naming an agent no
  ///     tab shows is dropped, and a beat must not be applied off the UI thread. A beat that
  ///     arrives while the cell is being created is HELD instead, because that is the ordinary
  ///     path - the spawner publishes the started fact and the loop's first beats back to back.
  ///     </para></summary>
  /// <param name="agentId">The agent the fact names.</param>
  /// <param name="fact">The fact to apply.</param>
  private void Route(AgentId agentId, ChildEvent fact)
  {
    _marshal(() =>
    {
      if (FindCell(agentId) is { } cell)
      {
        Apply(fact, cell);
        return;
      }

      lock (_heldGate)
      {
        if (_held.TryGetValue(agentId, out List<ChildEvent>? held))
        {
          held.Add(fact);
        }
      }
    });
  }

  /// <summary>Applies one fact to one cell.</summary>
  /// <param name="fact">The fact to apply.</param>
  /// <param name="cell">The cell it belongs to.</param>
  private static void Apply(ChildEvent fact, AgentCellViewModel cell)
  {
    switch (fact)
    {
      case ChildSettledEvent:
        cell.MarkFinished();
        break;
      case ChildProgressEvent progress:
        cell.ApplyPhaseBeat(progress.Label);
        break;
      case ChildIdleAlertEvent idle:
        cell.ApplyAlert(IdleNotice(idle));
        break;
      case ChildBudgetAlertEvent budget:
        cell.ApplyAlert(BudgetNotice(budget));
        break;
      default:
        break;
    }
  }

  /// <summary>The cell showing <paramref name="agentId" /> in ANY open tab, or null when no tab
  ///     shows it.
  ///     <para>EVERY TAB IS SEARCHED because a beat or an alert names only an agent: unlike a
  ///     started fact, whose record carries the workspace, there is nothing here to resolve the
  ///     tab from. A cell's identity is its agent, so the search is a lookup rather than a
  ///     guess.</para></summary>
  /// <param name="agentId">The agent whose cell to find.</param>
  private AgentCellViewModel? FindCell(AgentId agentId)
  {
    foreach (AgentTabViewModel tab in _openTabs())
    {
      if (tab.FindCell(agentId) is { } cell)
      {
        return cell;
      }
    }

    return null;
  }

  /// <summary>The one line an idle alert states in its cell's transcript.</summary>
  /// <param name="idle">The alert, whose idle age is the fact worth reading.</param>
  private static string IdleNotice(ChildIdleAlertEvent idle)
    => string.Create(CultureInfo.InvariantCulture,
        $"this agent has been idle for {(int)idle.IdleAge.TotalMinutes}m (last phase {idle.LastPhase}); the harness will nudge it.");

  /// <summary>The one line a budget alert states in its cell's transcript.</summary>
  /// <param name="budget">The alert, whose kind and consumption are the facts worth reading.</param>
  private static string BudgetNotice(ChildBudgetAlertEvent budget)
    => string.Create(CultureInfo.InvariantCulture,
        $"this agent has consumed {budget.Consumed:0.#} {budget.BudgetKind} (ceiling {(budget.Ceiling is { } ceiling ? ceiling.ToString("0.#", CultureInfo.InvariantCulture) : "none")}, burn {budget.BurnRatePerMinute:0.#}/min).");

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
