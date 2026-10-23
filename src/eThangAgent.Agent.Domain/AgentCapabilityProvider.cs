using System.Collections.Frozen;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The agent capability surface: spawn starts children as independent actors,
///     status and result retrieve their outcomes. Spawn renders only the running line —
///     reports arrive exclusively through the queries.</summary>
public sealed class AgentCapabilityProvider : ICapabilityProvider
{
  public const string ProviderId = "agent";

  /// <summary>The action ids this provider exposes, without constructing it — the
  ///     grant-surface computation needs the names at composition time, where
  ///     resolving the provider itself would re-enter its singleton (R1).</summary>
  public static readonly string[] ActionNames =
      ["spawn", "status", "result", "wait", "send", "resume", "cancel", "route", "escalate", "fanout",
        "notify-subtree", "notify-ancestors"];

  /// <summary>Builds the dispatch map eagerly: every handler closes over this
  ///     provider's collaborators, so the map is per provider (one per session).</summary>
  public AgentCapabilityProvider(AgentProviderServices services)
  {
    ArgumentNullException.ThrowIfNull(services);
    _spawnCommand = services.SpawnCommand ?? throw new ArgumentNullException(nameof(services), "SpawnCommand must not be null.");
    _queries = services.Queries ?? throw new ArgumentNullException(nameof(services), "Queries must not be null.");
    _parentContext = services.ParentContext ?? throw new ArgumentNullException(nameof(services), "ParentContext must not be null.");
    _runtime = services.Runtime;
    _links = services.Links;
    _fanout = services.Fanout;
    _locator = services.Locator;
    _eventsFor = services.EventsFor;
    _wakeSource = services.WakeSource;
    _clock = services.Clock;
    _handlers = BuildHandlers();
  }

  private readonly IAgentSpawnCommand _spawnCommand;
  private readonly IAgentQueries _queries;
  private readonly Func<AgentRecord> _parentContext;
  private readonly IAgentRuntime? _runtime;
  private readonly AgentLinkRegistry? _links;
  private readonly Func<AgentRecord, SpawnRequest[], CancellationToken, Task<string>>? _fanout;
  private readonly IAgentMailboxLocator? _locator;
  private readonly Func<AgentId, IAgentEvents?>? _eventsFor;
  private readonly IWaitWakeSource? _wakeSource;
  private readonly TimeProvider? _clock;
  private readonly FrozenDictionary<string, ActionHandler> _handlers;

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("spawn", "Spawn a child agent that runs autonomously in the background and returns immediately.",
            """
            Starts a child agent on a self-contained task and returns right away — never wait on the spawn call itself. Continue useful work or fan out siblings; when you need a child's outcome, use agent.wait (one await) — status is a projection for humans, not a poll target. Children may spawn their own children; depth limit is 3.
            Start failures return canonical error lines: InvalidSpawnRequest, DepthExceeded, MissingModel, ConcurrencyCapReached.
            Optional workspaceRoot anchors the child's exec scripts at a directory: it must be a non-empty ABSOLUTE path to an EXISTING directory inside the parent's effective root (the parent's own anchor for grandchild chains, else the session workspace); relative paths, missing directories, and outside-of-root paths are refused before the child starts. The anchor re-roots the child's ENTIRE tool surface: exec scripts resolve Workspace at the anchor AND every path-rooted tool (read/write/edit/git_status/git_commit/working_diff/worktree/sqlite_query and any workspace-relative file tool) resolves paths at the anchor - paths outside it are refused with PathOutsideWorkspace. An anchored parent spawns anchored children by default: a child spawned WITHOUT workspaceRoot inherits the parent's anchor.
            Anchor failures return: AnchorMissing (empty/relative/nonexistent path), AnchorInvalid (resolves outside the effective root, or no root establishes containment).
            Optional isolateInWorktree (boolean) provisions a FRESH worktree of the session repository inside the parent's effective root and anchors the child at it - the child's whole tool surface (files, exec, git) lands in the worktree and its git commits land on the worktree's branch, never the controller's checkout. Mutually exclusive with workspaceRoot; a failed provision refuses the spawn before it starts. Removal stays manual via the worktree tool.
            Output contract:
            id=<id> status=running|queued
            status=queued means the runtime's concurrent-agent limit parked the child: it will start on its own when a slot frees, agent.wait on it blocks normally, and agent.cancel stops it. Never re-spawn a queued child.
            """,
            [
                new ActionParameter("taskPrompt", ActionParameterTypes.StringType, "Self-contained task for the child. State exactly what the report must contain."),
                new ActionParameter("model", ActionParameterTypes.StringType, "Optional provider model reference; omit to use the configured default."),
                new ActionParameter("label", ActionParameterTypes.StringType, "Optional free-text label for humans and logs."),
                new ActionParameter("grants", ActionParameterTypes.StringType, "Optional capability grant: {\"tool.allow\": \"read; exec\", \"tool.deny\": \"web_fetch\"} (entries also accept string arrays). A granted child physically holds ONLY these tools plus agent actions; any other dispatch returns Error [GrantViolation] and is audited. Denying or omitting exec leaves the child no path to harness tools — grant exec unless the child needs none."),
                new ActionParameter("workspaceRoot", ActionParameterTypes.StringType, "Optional absolute path to an existing directory inside the parent's effective root; anchors the child's ENTIRE tool surface there (exec Workspace AND path-rooted tools; handler-validated: AnchorMissing / AnchorInvalid on violation). An anchored child spawned without workspaceRoot inherits the parent's anchor."),
                new ActionParameter("isolateInWorktree", ActionParameterTypes.BooleanType, "Optional; provision a fresh worktree inside the parent's effective root and anchor the child at it. Mutually exclusive with workspaceRoot; the child's git commits land on the worktree's branch, never the controller's checkout."),
            ]),
        new("status", "Projection of a spawned child agent's current state, for humans and debugging.",
            """
            Returns the child's current state as one annotation line. This is a projection, not a mechanism: when you need the outcome, use agent.wait (one await) instead of polling status.
            Output contract:
            id=<id> status=running
            id=<id> status=queued
            id=<id> status=completed
            id=<id> status=failed reason=max-iterations|timeout|provider-error|interrupted|hung|budget-exhausted|invalid-result
            id=<id> status=interrupted
            status=queued means the child is parked at the runtime's concurrent-agent limit: it has no run yet and starts on its own when a slot frees. The reason suffix appears only when status=failed.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
            ]),
        new("result", "Fetch the final report of a spawned child agent.",
            """
            Returns the child's final report verbatim once it has finished. While it is still running you receive 'Error [NotComplete]' — use agent.wait to await the outcome instead of re-polling. Unknown ids yield 'Error [NotFound]'. A failed child yields its partial report, or an Error [MaxIterations|Timeout|ProviderError] annotation when no report landed.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
            ]),
        new("wait", "Wait for a spawned child agent to settle and return its outcome as one result.",
            """
            Blocks until the child settles and returns the outcome contract of agent.result (the report, or the failure annotation). Unbounded by design: the watchdog guards the child, and your own stop cancels the wait with 'Error [Cancelled]'. Unknown ids yield 'Error [NotFound]'. Prefer one wait over repeated status polls when you need the outcome.
            Optional wakeOn (default "message"): "all" or a comma list of message|idle|settled|state|stream|budget|mailbox — the wait returns EARLY when a matching event arrives, WITHOUT the child settling. There is deliberately NO progress reason: a child's progress beats fire on every tool call, so a wait that woke on them would never rest. Optional timeoutSeconds (positive integer; must fit inside your exec call's own timeoutSeconds budget): a soft cap — expiry returns a wake annotation, never an error.
            USER STEERING ALWAYS WAKES: if the user sends a message while you wait, the wait returns immediately with reason=user-message, regardless of wakeOn. Read it and act (steer the child with agent.send, re-wait, or intervene).
            Woken return contract (child still running): [wait: woken reason=<user-message|idle|stream-stall|state|budget|mailbox|timeout>] then id=<id> status=running, then one digest line per event. On a wake you hold the turn again: steer, re-wait, or do other work; the child's eventual outcome is still retrievable with a later wait or agent.result.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
                new ActionParameter("wakeOn", ActionParameterTypes.StringType, "Optional: 'all' or comma list of message|idle|settled|state|stream|budget|mailbox (default 'message'). Matching events wake the wait early with the child still running. There is no progress reason — a child's progress beats fire on every tool call, so waking on them would never let the wait rest."),
                new ActionParameter("timeoutSeconds", ActionParameterTypes.IntegerType, "Optional soft cap in seconds; expiry returns a [wait: woken reason=timeout] annotation with the child still running — never an error. Must fit inside the exec call's own timeoutSeconds budget."),
            ]),
        new("send", "Send a steering message to one of your running child agents mid-run.",
            """
            Delivers into the child's mailbox; the child drains it at its next safe point (iteration boundary; never between a tool call and its result). Normal urgency drains at boundaries. Fails to you: NotRunning (unknown/finished child), MailboxFull (child's box at capacity — batch or retry), InvalidMessage (empty text). Content lands in the child's transcript on drain, attributed to you.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
                new ActionParameter("text", ActionParameterTypes.StringType, "Steering message for the child. Non-empty."),
                new ActionParameter("urgency", ActionParameterTypes.StringType, "Optional: normal (default) | attention | urgent."),
            ]),
        new("resume", "Resume a settled child agent with a new message (same id, same context).",
            """
            Continues a SETTLED child (completed or failed) on the SAME id: its persisted transcript,
            workspace anchor, and grants carry over; 'text' becomes the child's next instruction.
            Returns id=<id> status=running — await the fresh outcome with agent.wait, read it with
            agent.result. Fails: NotRunning (unknown id, or the child is still running — steer that
            with agent.send), InvalidMessage (empty text). Every fix round on a settled child beats
            re-spawning from scratch.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the settled child agent, exactly as returned by spawn."),
                new ActionParameter("text", ActionParameterTypes.StringType, "The continuation instruction for the child. Non-empty."),
            ]),
        new("cancel", "Interrupt a running child agent by id (the model-facing stop).",
            """
            Cancels one RUNNING child mid-run: the run's cancellation token fires, the loop repairs any half-finished tool batch, and the child settles as interrupted - its partial transcript stays retrievable with agent.result, and agent.resume can restart it. A QUEUED child (parked at the concurrency limit, no run yet) is removed from the queue and settles as failed reason=interrupted. Routed through the runtime's explicit-interrupt path (one of the three sanctioned cancellation sources). Fails to you: NotRunning (unknown id, or the child already settled - nothing to cancel), NotAvailable (no runtime wired).
            Output contract:
            interrupted id=<id>
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
            ]),
        new("route", "Send a message to a linked agent outside your local tree.",
            """
            Resolves 'name' through the session's consented link registry and delivers 'text' to that agent's runtime mailbox. Isolation by default: an unlinked name fails Error [NotLinked]. Unknown or finished targets fail Error [NotRunning]; a full mailbox fails Error [MailboxFull]. Receipt: delivered to=<address> link=<name>.
            """,
            [
                new ActionParameter("name", ActionParameterTypes.StringType, "The linked agent's registry name."),
                new ActionParameter("text", ActionParameterTypes.StringType, "Message text. Non-empty."),
                new ActionParameter("urgency", ActionParameterTypes.StringType, "Optional: normal (default) | attention | urgent."),
            ]),
        new("escalate", "Send a message upward: to your parent, or N hops up the agent tree.",
            """
            Delivers 'text' to your parent agent (hops=1, the default); hops=N walks N ancestor links. Every hop emits a delivery event and the result lists per-hop receipts: hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull. The walk stops at the tree root with receipt reached=root.
            """,
            [
                new ActionParameter("text", ActionParameterTypes.StringType, "Message text. Non-empty."),
                new ActionParameter("hops", ActionParameterTypes.IntegerType, "Optional ancestor hops (default 1, minimum 1)."),
                new ActionParameter("urgency", ActionParameterTypes.StringType, "Optional: normal (default) | attention | urgent."),
            ]),
        new("notify-subtree", "Broadcast one message to every live descendant of yours.",
            """
            Walks your persisted descendant links downward (the same chain a subtree
            interrupt tears down) and push-delivers 'text' into every live descendant's
            mailbox - grandchildren included. Best-effort, per-target receipts, never
            retried: settled or foreign ids report NotRunning and are skipped. The result
            lists one receipt per target - hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull
            (hop counts tree depth below you, direct children are hop=1) - then a summary
            line: reached=<count> delivered=<count>. An empty subtree succeeds with
            reached=0 delivered=0.
            """,
            [
                new ActionParameter("text", ActionParameterTypes.StringType, "Message text for every descendant. Non-empty."),
                new ActionParameter("urgency", ActionParameterTypes.StringType, "Optional: normal (default) | attention | urgent."),
            ]),
        new("notify-ancestors", "Broadcast one message to every ancestor, all the way to the root.",
            """
            The sibling of escalate without the hop bound: delivers 'text' to your parent,
            grandparent, ... up to the root, using escalate's per-hop receipt format -
            hop=<n> to=<agent-id> delivered|NotRunning|MailboxFull - then closes with
            reached=root delivered=<count>. A receiver whose row is gone reports NotRunning;
            the walk continues to the root regardless.
            """,
            [
                new ActionParameter("text", ActionParameterTypes.StringType, "Message text for every ancestor. Non-empty."),
                new ActionParameter("urgency", ActionParameterTypes.StringType, "Optional: normal (default) | attention | urgent."),
            ]),
        new("fanout", "Spawn several children in one call and wait for ALL of them.",
            """
            Fan-out/fan-in: spawns every child described in 'children' (same shape as agent.spawn, one object each), waits for the whole set to settle, and joins. Per-member receipts name each child's id and terminal state; failed STARTS fail the join immediately; settled failures are collected and fail the join at the end. Receipts: <id>=COMPLETED|FAILED(reason).
            Each child spec additionally accepts an optional workspaceRoot (absolute path to an existing directory inside the parent's effective root) — same anchor semantics and AnchorMissing / AnchorInvalid refusals as agent.spawn — or isolateInWorktree (boolean; a fresh worktree is provisioned inside the parent's effective root and the child anchored at it; mutually exclusive with that child's workspaceRoot); an invalid anchor in ANY child fails the fan-out's start.
            """,
            [
                new ActionParameter("label", ActionParameterTypes.StringType, "Optional label prefix for the graph."),
                new ActionParameter("children", ActionParameterTypes.StringType, "JSON array of child specs: [{\"taskPrompt\":\"...\",\"model\":\"...\",\"label\":\"...\",\"workspaceRoot\":\"C:\\\\abs\\\\path\",\"isolateInWorktree\":true}] - taskPrompt required per child; optional workspaceRoot anchors that child's ENTIRE tool surface - exec scripts AND path-rooted tools resolve at the anchor; a child spawned without workspaceRoot inherits the parent's anchor (AnchorMissing / AnchorInvalid on violation); optional isolateInWorktree provisions a fresh worktree and anchors the child at it, mutually exclusive with workspaceRoot."),
            ]),
    ];

  private delegate Task<CapabilityInvocationResult> ActionHandler(string json, CancellationToken ct);

  private FrozenDictionary<string, ActionHandler> BuildHandlers()
  {
    return new Dictionary<string, ActionHandler>(StringComparer.Ordinal)
    {
      ["spawn"] = Spawn,
      ["status"] = Status,
      ["result"] = GetResult,
      ["wait"] = Wait,
      ["send"] = (json, _) => Send(json),
      ["resume"] = (json, _) => Resume(json),
      ["cancel"] = Cancel,
      ["route"] = (json, _) => Task.FromResult(Route(json)),
      ["escalate"] = Escalate,
      ["notify-subtree"] = NotifySubtree,
      ["notify-ancestors"] = NotifyAncestors,
      ["fanout"] = Fanout,
    }.ToFrozenDictionary();
  }

  public Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    return _handlers.TryGetValue(actionName, out ActionHandler? handler)
        ? handler(jsonArguments, ct)
        : Task.FromResult(CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."));
  }

  /// <summary>Starts the child as an independent actor and returns immediately. The report is
  ///     retrieved later through agent.status / agent.result; failures of the start itself are
  ///     passed through as canonical error lines.</summary>
  private async Task<CapabilityInvocationResult> Spawn(string json, CancellationToken ct)
  {
    (SpawnRequest? request, string? parseError) = ParseArgs(json);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    Result<AgentId> started = await _spawnCommand.Execute(_parentContext(), request!, ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{started.Error.Code}]: {started.Error.Message}");
    }

    // Honesty rule (issue #238): the receipt states the state the child is ACTUALLY in.
    // A spawn parked at the concurrency boundary is queued, not running; reporting it as
    // running would tell the caller a run exists when none has begun.
    return CapabilityInvocationResult.Ok($"id={started.Value} status={await SpawnedStateWordAsync(started.Value, ct).ConfigureAwait(false)}");
  }

  /// <summary>The status word for a just-spawned child: its persisted state, or
  ///     "running" when the row cannot be read. The spawn itself succeeded, so a
  ///     projection fault (an unreadable row, a wiring without status queries) must
  ///     never turn a successful spawn into an error — the receipt falls back to the
  ///     pre-existing word.</summary>
  private async Task<string> SpawnedStateWordAsync(AgentId id, CancellationToken ct)
  {
    try
    {
      Result<AgentRecord> record = await _queries.GetStatus(id, ct).ConfigureAwait(false);
      return record.IsSuccess ? StatusWord(record.Value.Status) : "running";
    }
    // Named decision (CA1031): the projection is a nicety on an already-successful
    // spawn; a query fault degrades the receipt word, never the spawn result.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return "running";
    }
  }

  /// <summary>Await the child's terminal transition and render the outcome contract of
  ///     agent.result: the report verbatim on success, the partial report (or the failure
  ///     annotation) on failure. NotFound/Cancelled pass through untouched.</summary>
  private async Task<CapabilityInvocationResult> Wait(string json, CancellationToken ct)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.wait needs a runtime wired into this session's capability provider.");
    }

    Result<AgentId> id = ParseIdArgument(json);
    if (!id.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{id.Error.Code}]: {id.Error.Message}");
    }

    string? parseError = WaitArgs(json, out WakeFilter filter, out TimeSpan? timeout);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    // Legacy path: no wake source AND no timeout — the pure WhenSettledAsync await,
    // byte-identical output (the default wakeOn filter accepts only the user wake,
    // which cannot fire without a source).
    if (_wakeSource is null && timeout is null)
    {
      return await LegacyWait(_runtime, id.Value, ct).ConfigureAwait(false);
    }

    WakeAwareWait wait = new(_wakeSource ?? NullWaitWakeSource.Instance,
        token => _runtime.WhenSettledAsync(id.Value, token), _clock ?? TimeProvider.System);
    WaitResult result = await wait.RunAsync(new WaitSpec(id.Value, filter, timeout), ct).ConfigureAwait(false);

    return result.Error is { } error
        ? CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}")
        : RenderWaitOutcome(id.Value, result);
  }

  /// <summary>Renders a wake-aware wait's non-error result: the settled outcome
  ///     contract, or the woken/expired rendering with the child still running.</summary>
  private static CapabilityInvocationResult RenderWaitOutcome(AgentId id, WaitResult result)
    => result.Settled ? RenderOutcome(result.Outcome!) : RenderWake(id, result);

  /// <summary>Renders a woken/expired wait: the wake annotation naming the first
  ///     reason (timeout expiry renders as reason=timeout), the still-running line,
  ///     then the accumulated digest lines oldest first.</summary>
  private static CapabilityInvocationResult RenderWake(AgentId id, WaitResult result)
  {
    string reasonText = result.Reason is { } reason ? ReasonWord(reason) : "timeout";
    List<string> lines =
    [
      $"[wait: woken reason={reasonText}]",
      $"id={id} status=running",
    ];
    lines.AddRange(result.DigestLines);
    return CapabilityInvocationResult.Ok(string.Join("\n", lines));
  }

  /// <summary>The legacy wait path: pure settlement await, today's rendering verbatim.</summary>
  private static async Task<CapabilityInvocationResult> LegacyWait(IAgentRuntime runtime, AgentId id, CancellationToken ct)
  {
    Result<AgentRunOutcome> outcome = await runtime.WhenSettledAsync(id, ct).ConfigureAwait(false);
    return outcome.IsSuccess
        ? RenderOutcome(outcome.Value)
        : CapabilityInvocationResult.Fail($"Error [{outcome.Error.Code}]: {outcome.Error.Message}");
  }

  /// <summary>Renders a settled outcome. Honesty rule (issue #124): only a COMPLETED
  ///     settlement renders its report as success — a failed outcome's report field can
  ///     carry the failure detail (or any partial text), and rendering it as the child's
  ///     deliverable is exactly how agent.wait once handed the parent a failure sentence
  ///     as if it were the task's result. Failure annotations pass through untouched.</summary>
  private static CapabilityInvocationResult RenderOutcome(AgentRunOutcome settled)
  {
    return settled.Status is AgentStatus.Completed
        ? CapabilityInvocationResult.Ok(settled.Report)
        : CapabilityInvocationResult.Fail("Error [" + ReasonText(settled.Reason) + "]: the child settled without a usable report.");
  }

  /// <summary>Strict wait-argument parsing beyond the id: wakeOn (default "message") and
  ///     timeoutSeconds (positive integer). Unknown tokens and non-positive timeouts are
  ///     InvalidActionInput naming the offending member.</summary>
  private static string? WaitArgs(string json, out WakeFilter filter, out TimeSpan? timeout)
  {
    filter = WakeFilter.Parse(null).Value!;
    timeout = null;
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException ex)
    {
      return $"arguments must be a JSON object ({ex.Message}).";
    }

    using (doc)
    {
      if (doc.RootElement.TryGetProperty("wakeOn", out JsonElement wakeElement))
      {
        if (wakeElement.ValueKind is not JsonValueKind.String)
        {
          return "'wakeOn' must be a string.";
        }

        Result<WakeFilter> parsed = WakeFilter.Parse(wakeElement.GetString());
        if (!parsed.IsSuccess)
        {
          return parsed.Error.Message;
        }

        filter = parsed.Value;
      }

      if (doc.RootElement.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement))
      {
        if (timeoutElement.ValueKind is not JsonValueKind.Number || !timeoutElement.TryGetInt32(out int seconds))
        {
          return "'timeoutSeconds' must be an integer.";
        }

        if (seconds <= 0)
        {
          return "'timeoutSeconds' must be a positive integer.";
        }

        timeout = TimeSpan.FromSeconds(seconds);
      }

      return null;
    }
  }

  private static string ReasonWord(WakeReason reason) => reason switch
  {
    WakeReason.UserMessage => "user-message",
    WakeReason.Idle => "idle",
    WakeReason.StreamStall => "stream-stall",
    WakeReason.StateWrite => "state",
    WakeReason.Budget => "budget",
    WakeReason.Mailbox => "mailbox",
    _ => "unknown",
  };
  /// <summary>agent.send: parent-to-child steering through the runtime's push-delivery.
  ///     Delivery to self is rejected at validation; unknown targets surface NotFound;
  ///     overflow surfaces MailboxFull — every failure has a recipient (A3). A COMPLETED
  ///     child is auto-resumed with the message as its next instruction (the resume
  ///     contract's receipt); a Failed/Interrupted child is refused with NotRunning —
  ///     messaging it cannot restart it, agent.resume is the explicit restart path.</summary>
  private async Task<CapabilityInvocationResult> Send(string json)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.send needs a runtime wired into this session's capability provider.");
    }

    string? parseError = SendArgs(json, out AgentId? id, out string? text, out MessageUrgency urgency);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    AgentRecord parent = _parentContext();
    if (id!.Value == parent.Id)
    {
      return CapabilityInvocationResult.Fail("Error [InvalidActionInput]: an agent cannot send to itself.");
    }

    // A settled target changes the send's meaning: the user messaging a done child
    // wants it to keep going, so a completed child resumes with the text verbatim,
    // while a failed/interrupted child cannot be restarted by a message (refusal).
    Result<AgentRecord> target = await _queries.GetStatus(id.Value, CancellationToken.None).ConfigureAwait(false);
    CapabilityInvocationResult? settled = target.IsSuccess
        ? await SettledSendOutcome(_runtime, id.Value, target.Value.Status, text!, CancellationToken.None).ConfigureAwait(false)
        : null;
    if (settled is not null)
    {
      return settled;
    }

    Result<bool> delivered = _runtime.Deliver(id.Value,
        new PendingMessage(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(parent)));
    return delivered.IsSuccess
        ? CapabilityInvocationResult.Ok($"delivered to={id.Value} urgency={urgency.ToString().ToUpperInvariant()}")
        : CapabilityInvocationResult.Fail($"Error [{delivered.Error.Code}]: {delivered.Error.Message}");
  }

  /// <summary>The settled-target half of agent.send: a completed child auto-resumes
  ///     with the text verbatim (the resume contract's receipt); a failed/interrupted
  ///     child is refused — messaging it cannot restart it, agent.resume is the
  ///     explicit restart path. A running child returns null: the ordinary delivery
  ///     path below owns it.</summary>
  private static async Task<CapabilityInvocationResult?> SettledSendOutcome(IAgentRuntime runtime, AgentId id, AgentStatus status, string text, CancellationToken ct)
  {
    switch (status)
    {
      case AgentStatus.Completed:
        Result<AgentId> resumed = await runtime.Resume(id, text, ct).ConfigureAwait(false);
        return resumed.IsSuccess
            ? CapabilityInvocationResult.Ok($"id={resumed.Value} status=running")
            : CapabilityInvocationResult.Fail($"Error [{resumed.Error.Code}]: {resumed.Error.Message}");
      case AgentStatus.Failed:
      case AgentStatus.Interrupted:
        return CapabilityInvocationResult.Fail(
            $"Error [NotRunning]: agent {id} is {StatusWord(status)}; messaging it cannot restart it — use agent.resume to restart it explicitly.");
      case AgentStatus.Running:
      case AgentStatus.Queued:
      default:
        return null; // the ordinary delivery path owns a running or queued target
    }
  }

  /// <summary>agent.resume: same-id fix-round continuation of a SETTLED child. Reuses
  ///     the send-argument parsing (id + text; no urgency). Every failure has a recipient:
  ///     NotRunning (unknown/still-running/foreign), InvalidMessage, and ResumeUnsupported
  ///     on remote runtimes.</summary>
  private async Task<CapabilityInvocationResult> Resume(string json)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.resume needs a runtime wired into this session's capability provider.");
    }

    string? parseError = SendArgs(json, out AgentId? id, out string? text, out MessageUrgency _);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    Result<AgentId> resumed = await _runtime.Resume(id!.Value, text!, CancellationToken.None).ConfigureAwait(false);
    return resumed.IsSuccess
        ? CapabilityInvocationResult.Ok($"id={resumed.Value} status=running")
        : CapabilityInvocationResult.Fail($"Error [{resumed.Error.Code}]: {resumed.Error.Message}");
  }

  /// <summary>agent.cancel (issue #94): the model-facing stop for one running child. The
  ///     unknown/settled matrix is checked BEFORE the interrupt (an interrupt of an
  ///     unknown id is a silent no-op at the runtime seam, which would read as success),
  ///     and the interrupt itself rides the runtime's explicit-interrupt path - one of
  ///     the three sanctioned cancellation sources. The receipt names the id so the
  ///     caller can follow up with agent.result.</summary>
  private async Task<CapabilityInvocationResult> Cancel(string json, CancellationToken ct)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.cancel needs a runtime wired into this session's capability provider.");
    }

    Result<AgentId> id = ParseIdArgument(json);
    if (!id.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{id.Error.Code}]: {id.Error.Message}");
    }

    Result<AgentRecord> target = await _queries.GetStatus(id.Value, ct).ConfigureAwait(false);
    if (!target.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{target.Error.Code}]: {target.Error.Message}");
    }

    // A QUEUED child is cancellable too (issue #238): it holds no slot yet, but it is
    // owned by the runtime and agent.cancel is the only surface that can stop it —
    // refusing here would leave a parked spawn with no way out but a blanket interrupt.
    if (target.Value.Status is not (AgentStatus.Running or AgentStatus.Queued))
    {
      return CapabilityInvocationResult.Fail(
          $"Error [NotRunning]: agent {id.Value} is {StatusWord(target.Value.Status)}; nothing to cancel.");
    }

    _runtime.Interrupt(id.Value);
    return CapabilityInvocationResult.Ok($"interrupted id={id.Value}");
  }

  private static string SenderLabel(AgentRecord record)
      => string.IsNullOrEmpty(record.Label) ? "parent" : "parent:" + record.Label;

  /// <summary>Strict send-argument parsing: id (Guid D), non-empty text, urgency within
  ///     the enum range. Anything else returns a validation error naming the member.</summary>
  private static string? SendArgs(string json, out AgentId? id, out string? text, out MessageUrgency urgency)
  {
    id = null;
    text = null;
    urgency = MessageUrgency.Normal;
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException ex)
    {
      return $"arguments must be a JSON object ({ex.Message}).";
    }

    // Deliberately no object-kind gate and no unknown-member rejection here: send
    // predates the strict Args contract and resume reuses it — the id check owns a
    // non-object root (byte-identical legacy behavior).
    using (doc)
    {
      if (!doc.RootElement.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind is not JsonValueKind.String
          || !Guid.TryParseExact(idElement.GetString(), "D", out Guid parsed))
      {
        return "'id' must be a GUID string.";
      }

      id = new AgentId(parsed);
      return RequireNonEmpty(doc.RootElement, "text", out text)
          ?? TryReadUrgency(doc.RootElement, out urgency);
    }
  }
  private async Task<CapabilityInvocationResult> Status(string json, CancellationToken ct)
  {
    Result<AgentId> id = ParseIdArgument(json);
    if (!id.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{id.Error.Code}]: {id.Error.Message}");
    }

    Result<AgentRecord> lookup = await _queries.GetStatus(id.Value, ct).ConfigureAwait(false);
    return lookup.IsSuccess
        ? CapabilityInvocationResult.Ok(StateLine(lookup.Value))
        : CapabilityInvocationResult.Fail($"Error [{lookup.Error.Code}]: {lookup.Error.Message}");
  }

  private async Task<CapabilityInvocationResult> GetResult(string json, CancellationToken ct)
  {
    Result<AgentId> id = ParseIdArgument(json);
    if (!id.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{id.Error.Code}]: {id.Error.Message}");
    }

    Result<string> report = await _queries.GetResult(id.Value, ct).ConfigureAwait(false);
    return report.IsSuccess
        ? CapabilityInvocationResult.Ok(report.Value)
        : CapabilityInvocationResult.Fail($"Error [{report.Error.Code}]: {report.Error.Message}");
  }

  /// <summary>Renders the status output contract: the state line, plus the reason suffix
  ///     exactly when status=failed. A Failed row without a reason violates the record
  ///     invariant and aborts loudly rather than inventing output.</summary>
  private static string StateLine(AgentRecord record) => record.Status switch
  {
    AgentStatus.Running => $"id={record.Id} status=running",
    AgentStatus.Completed => $"id={record.Id} status=completed",
    AgentStatus.Failed => $"id={record.Id} status=failed reason={ReasonText(record.FailureReason)}",
    AgentStatus.Interrupted => $"id={record.Id} status=interrupted",
    AgentStatus.Queued => $"id={record.Id} status=queued",
    _ => throw new InvalidOperationException($"Unknown agent status '{record.Status}' for agent '{record.Id}'."),
  };

  /// <summary>Lowercase status words for the send-refusal contract (CA1308 forbids
  ///     ToLowerInvariant normalization; the words are pinned, not computed).</summary>
  private static string StatusWord(AgentStatus status) => status switch
  {
    AgentStatus.Running => "running",
    AgentStatus.Completed => "completed",
    AgentStatus.Failed => "failed",
    AgentStatus.Interrupted => "interrupted",
    AgentStatus.Queued => "queued",
    _ => throw new InvalidOperationException($"Unknown agent status '{status}'."),
  };

  private static string ReasonText(AgentFailureReason? reason) => reason switch
  {
    AgentFailureReason.MaxIterations => "max-iterations",
    AgentFailureReason.Timeout => "timeout",
    AgentFailureReason.ProviderError => "provider-error",
    AgentFailureReason.Interrupted => "interrupted",
    AgentFailureReason.Hung => "hung",
    AgentFailureReason.BudgetExhausted => "budget-exhausted",
    AgentFailureReason.InvalidResult => "invalid-result",
    _ => throw new InvalidOperationException($"Unknown agent failure reason '{reason}'."),
  };

  /// <summary>agent.route (R2.1): resolve a consented link by name, then push-deliver
  ///     to the linked agent's address. The registry reveals only the address (R2.4);
  ///     unresolved names surface the pinned NotLinked contract.</summary>
  private CapabilityInvocationResult Route(string json)
  {
    if (_runtime is null || _links is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.route needs a runtime and a link registry wired into this session's capability provider.");
    }

    string? parseError = RouteArgs(json, out string? name, out string? text, out MessageUrgency urgency);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    Result<LinkAddress> resolved = _links.Resolve(name!);
    if (!resolved.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{resolved.Error.Code}]: {resolved.Error.Message}");
    }

    if (!Guid.TryParseExact(resolved.Value.AgentAddress, "D", out Guid target))
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidLink]: link '{resolved.Value.Name}' does not address an agent id.");
    }

    AgentRecord sender = _parentContext();
    PendingMessage message = new(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(sender));
    AgentId targetId = new(target);

    Result<bool> delivered = DeliverToTarget(targetId, message, urgency, text!);
    return delivered.IsSuccess
        ? CapabilityInvocationResult.Ok($"delivered to={resolved.Value.AgentAddress} link={resolved.Value.Name}")
        : CapabilityInvocationResult.Fail($"Error [{delivered.Error.Code}]: {delivered.Error.Message}");
  }

  /// <summary>Delivers one route/send message to a target id: the local half first
  ///     (W3.1 — the session's own runtime owns its children's mailboxes), then the
  ///     cross-container half (W3.2) when the local runtime reports NotRunning and
  ///     the process-wide locator resolves the OWNING container's live mailbox.</summary>
  private Result<bool> DeliverToTarget(AgentId targetId, PendingMessage message, MessageUrgency urgency, string text)
  {
    Result<bool> delivered = _runtime!.Deliver(targetId, message);
    if (!delivered.IsSuccess
        && MailboxErrors.NotRunning.Equals(delivered.Error.Code, StringComparison.Ordinal)
        && _locator?.TryGet(targetId) is { } foreign)
    {
      delivered = DeliverCrossContainer(foreign, targetId, message, urgency, text);
    }

    return delivered;
  }

  /// <summary>The cross-container delivery half of agent.route (W3.2): the local
  ///     runtime holds no mailbox for the target, so the process-wide locator resolved
  ///     the OWNING container's live mailbox. Delivery and receipt contracts are
  ///     identical to the local half; the audit event marks the delivery
  ///     cross-container on the target's stream (3.3).</summary>
  private Result<bool> DeliverCrossContainer(IAgentMailbox foreign, AgentId targetId, PendingMessage message, MessageUrgency urgency, string text)
  {
    Result<bool> delivered = foreign.Deliver(message);
    if (delivered.IsSuccess)
    {
      // The audit event publishes on the OWNER's stream — the resolver returns the
      // target container's own event stream (3.3: the host-side audit trail reads it
      // where the target's host listens), never the sender's.
      _eventsFor?.Invoke(targetId)?.Publish(new MessageDeliveredEvent(targetId,
          DateTimeOffset.UtcNow, "cross-container", (int)urgency,
          System.Text.Encoding.UTF8.GetByteCount(text)));
    }

    return delivered;
  }

  /// <summary>agent.escalate (R2.2): walk up to N ancestor links, one delivery per hop,
  ///     every hop rendered as a receipt (FR-C7 per-hop visibility). The walk stops
  ///     early at the root with reached=root.</summary>
  private async Task<CapabilityInvocationResult> Escalate(string json, CancellationToken ct)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.escalate needs a runtime wired into this session's capability provider.");
    }

    string? parseError = EscalateArgs(json, out string? text, out int hops, out MessageUrgency urgency);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    AgentRecord sender = _parentContext();
    List<string> receipts = [];
    AgentId? current = sender.ParentId;
    for (int hop = 1; hop <= hops; hop++)
    {
      if (current is null)
      {
        receipts.Add($"reached=root at hop={hop}");
        break;
      }

      Result<AgentRecord> ancestor = await _queries.GetStatus(current.Value, ct).ConfigureAwait(false);
      if (!ancestor.IsSuccess)
      {
        receipts.Add($"hop={hop} to={current.Value} NotRunning");
        break;
      }

      Result<bool> delivered = _runtime.Deliver(current.Value,
          new PendingMessage(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(sender)));
      receipts.Add(delivered.IsSuccess
          ? $"hop={hop} to={current.Value} delivered"
          : $"hop={hop} to={current.Value} {delivered.Error.Code}");
      current = ancestor.Value.ParentId;
    }

    return CapabilityInvocationResult.Ok(string.Join("\n", receipts));
  }

  /// <summary>agent.notify-subtree (W4.1): one message to every live descendant.
  ///     The walk rides the store's parent-link index — the same chain
  ///     InterruptSubtree tears down — breadth-first so receipts read near-to-far.
  ///     Delivery goes through the runtime's push seam exactly like agent.send;
  ///     per-target failures render in escalate's receipt format and are never
  ///     retried (A1/A3). The unknown-parent walk terminates (FR-L8 honesty):
  ///     a pruned branch is skipped, never invented.</summary>
  private async Task<CapabilityInvocationResult> NotifySubtree(string json, CancellationToken ct)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.notify-subtree needs a runtime wired into this session's capability provider.");
    }

    string? parseError = NotifyArgs(json, out string? text, out MessageUrgency urgency);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    AgentRecord sender = _parentContext();
    List<string> receipts = [];
    int delivered = 0;
    HashSet<Guid> seen = [sender.Id.Value];
    List<(AgentRecord Record, int Hop)> frontier = [];
    Result<IReadOnlyList<AgentRecord>> children = await _queries.ListChildrenAsync(sender.Id, ct).ConfigureAwait(false);
    if (children.IsSuccess)
    {
      frontier.AddRange(children.Value.Select(child => (child, 1)));
    }

    while (frontier.Count > 0)
    {
      List<(AgentRecord Record, int Hop)> next = [];
      foreach ((AgentRecord record, int hop) in frontier)
      {
        if (!seen.Add(record.Id.Value))
        {
          continue;
        }

        Result<bool> attempt = _runtime.Deliver(record.Id,
            new PendingMessage(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(sender)));
        if (attempt.IsSuccess)
        {
          delivered++;
        }

        receipts.Add($"hop={hop} to={record.Id} {(attempt.IsSuccess ? "delivered" : attempt.Error.Code)}");
        Result<IReadOnlyList<AgentRecord>> grandchildren = await _queries.ListChildrenAsync(record.Id, ct).ConfigureAwait(false);
        if (grandchildren.IsSuccess)
        {
          next.AddRange(grandchildren.Value.Select(grandchild => (grandchild, hop + 1)));
        }
      }

      frontier = next;
    }

    receipts.Add($"reached={receipts.Count} delivered={delivered}");
    return CapabilityInvocationResult.Ok(string.Join("\n", receipts));
  }

  /// <summary>agent.notify-ancestors (W4.2): escalate's walk and receipt semantics with
  ///     no hop bound — every ancestor up to the root receives the message. A pruned
  ///     parent row reports NotRunning and the walk continues; reached=root closes the
  ///     contract.</summary>
  private async Task<CapabilityInvocationResult> NotifyAncestors(string json, CancellationToken ct)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.notify-ancestors needs a runtime wired into this session's capability provider.");
    }

    string? parseError = NotifyArgs(json, out string? text, out MessageUrgency urgency);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    AgentRecord sender = _parentContext();
    List<string> receipts = [];
    int delivered = 0;
    AgentId? current = sender.ParentId;
    int hop = 1;
    while (current is { } target)
    {
      Result<AgentRecord> ancestor = await _queries.GetStatus(target, ct).ConfigureAwait(false);
      Result<bool> attempt = _runtime.Deliver(target,
          new PendingMessage(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(sender)));
      if (attempt.IsSuccess)
      {
        delivered++;
      }

      receipts.Add($"hop={hop} to={target} {(attempt.IsSuccess ? "delivered" : attempt.Error.Code)}");
      if (!ancestor.IsSuccess)
      {
        break; // pruned row: the walk cannot see past the gap; reached=root still closes
      }

      current = ancestor.Value.ParentId;
      hop++;
    }

    receipts.Add($"reached=root delivered={delivered}");
    return CapabilityInvocationResult.Ok(string.Join("\n", receipts));
  }

  /// <summary>Parses the raw arguments into a detached JSON object root: malformed
  ///     JSON and non-object roots both name the object requirement (the shared first
  ///     gate of every strict Args parser). The document is disposed here; the root
  ///     out-value is a clone that outlives it.</summary>
  private static string? RequireObject(string json, out JsonElement root)
  {
    root = default;
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException ex)
    {
      return $"arguments must be a JSON object ({ex.Message}).";
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind is not JsonValueKind.Object)
      {
        return "arguments must be a JSON object.";
      }

      root = doc.RootElement.Clone();
      return null;
    }
  }

  /// <summary>Rejects every member outside the allowed set, naming them verbatim
  ///     (the shared unknown-parameter gate).</summary>
  private static string? RejectUnknown(JsonElement root, params string[] allowed)
  {
    HashSet<string> allowedSet = new(allowed, StringComparer.Ordinal);
    string[] unknown = [.. root.EnumerateObject().Where(p => !allowedSet.Contains(p.Name)).Select(p => p.Name)];
    return unknown.Length > 0
        ? $"unknown parameter(s): {string.Join(", ", unknown)}."
        : null;
  }

  /// <summary>Reads one required non-empty string member, naming it on violation
  ///     (the shared text/name field gate).</summary>
  private static string? RequireNonEmpty(JsonElement root, string name, out string? value)
  {
    value = null;
    if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind is not JsonValueKind.String
        || string.IsNullOrWhiteSpace(element.GetString()))
    {
      return $"'{name}' must be a non-empty string.";
    }

    value = element.GetString();
    return null;
  }

  /// <summary>Reads the optional urgency member: absent keeps Normal; present must be
  ///     a string naming one of the enum values case-insensitively.</summary>
  private static string? TryReadUrgency(JsonElement root, out MessageUrgency urgency)
  {
    urgency = MessageUrgency.Normal;
    return root.TryGetProperty("urgency", out JsonElement urgencyElement)
        && (urgencyElement.ValueKind is not JsonValueKind.String
            || !Enum.TryParse(urgencyElement.GetString(), ignoreCase: true, out urgency))
        ? "'urgency' must be one of: normal, attention, urgent."
        : null;
  }

  /// <summary>Strict notify-argument parsing: non-empty text, urgency in range; no other
  ///     members accepted.</summary>
  private static string? NotifyArgs(string json, out string? text, out MessageUrgency urgency)
  {
    text = null;
    urgency = MessageUrgency.Normal;
    return RequireObject(json, out JsonElement root)
        ?? RejectUnknown(root, "text", "urgency")
        ?? RequireNonEmpty(root, "text", out text)
        ?? TryReadUrgency(root, out urgency);
  }

  /// <summary>agent.fanout (D12): fan-out/fan-in over the normal spawn command +
  ///     WhenSettledAsync. The graph seam is injected (implemented by the application
  ///     layer's SpawnGraphHandler — the domain cannot see application types).</summary>
  private async Task<CapabilityInvocationResult> Fanout(string json, CancellationToken ct)
  {
    if (_fanout is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.fanout needs the graph handler wired into this session's capability provider.");
    }

    string? parseError = FanoutArgs(json, out string? label, out List<SpawnRequest>? children);
    _ = label; // validated, then dormant: no production consumer reads SpawnGraphRequest.Label
    // (W5.2 finding, ledgered - the action advertises an optional label prefix that is never rendered)
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    AgentRecord parent = _parentContext();
    string result = await _fanout(parent, [.. children!], ct).ConfigureAwait(false);
    return CapabilityInvocationResult.Ok(result);
  }

  /// <summary>Strict fanout-argument parsing: non-empty children array of valid specs.</summary>
  private static string? FanoutArgs(string json, out string? label, out List<SpawnRequest>? children)
  {
    label = null;
    children = null;
    if ((RequireObject(json, out JsonElement root)
        ?? RejectUnknown(root, "label", "children")) is { } gateError)
    {
      return gateError;
    }

    if (!TryReadChildren(root, out JsonElement childrenElement, out string? childrenError))
    {
      return childrenError;
    }

    if (root.TryGetProperty("label", out JsonElement labelElement)
        && labelElement.ValueKind is not JsonValueKind.String)
    {
      return "'label' must be a string.";
    }

    label = root.TryGetProperty("label", out labelElement) ? labelElement.GetString() : null;

    List<SpawnRequest> parsed = [];
    int index = 0;
    foreach (JsonElement child in childrenElement.EnumerateArray())
    {
      index++;
      string? specError = ParseChildSpec(child, index, out SpawnRequest request);
      if (specError is not null)
      {
        return specError;
      }

      parsed.Add(request);
    }

    children = parsed;
    return null;
  }

  /// <summary>Reads the required children array: present, an array, and non-empty
  ///     (the caller renders the verbatim typed error otherwise).</summary>
  private static bool TryReadChildren(JsonElement root, out JsonElement childrenElement, out string? error)
  {
    childrenElement = default;
    error = null;
    if (!root.TryGetProperty("children", out childrenElement) || childrenElement.ValueKind is not JsonValueKind.Array)
    {
      error = "'children' must be an array of child specs.";
      return false;
    }

    if (childrenElement.GetArrayLength() == 0)
    {
      error = "'children' requires at least one child spec.";
      return false;
    }

    return true;
  }

  /// <summary>Parses one child spec object into its SpawnRequest, or the verbatim
  ///     per-member error naming the child's index (non-object specs included).</summary>
  private static string? ParseChildSpec(JsonElement child, int index, out SpawnRequest request)
  {
    request = new SpawnRequest("");
    if (child.ValueKind is not JsonValueKind.Object)
    {
      return $"children[{index}] must be an object.";
    }

    if (!child.TryGetProperty("taskPrompt", out JsonElement promptElement) || promptElement.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(promptElement.GetString()))
    {
      return $"children[{index}].taskPrompt must be a non-empty string.";
    }

    if (OptionalChildString(child, "model", $"children[{index}].model must be a string.", out string? model) is { } modelError)
    {
      return modelError;
    }

    if (OptionalChildString(child, "label", $"children[{index}].label must be a string.", out string? childLabel) is { } labelError)
    {
      return labelError;
    }

    if (OptionalChildRoot(child, $"children[{index}].workspaceRoot must be a non-empty string.",
            out string? childWorkspaceRoot) is { } rootError)
    {
      return rootError;
    }

    if (OptionalChildIsolate(child, $"children[{index}].isolateInWorktree must be a boolean.",
            out bool childIsolate) is { } isolateError)
    {
      return isolateError;
    }

    request = new SpawnRequest(promptElement.GetString()!, model, childLabel,
        WorkspaceRoot: childWorkspaceRoot, IsolateInWorktree: childIsolate);
    return null;
  }

  /// <summary>Validates one optional string member of a child spec: absent keeps
  ///     null, present-but-not-a-string is the given typed error.</summary>
  private static string? OptionalChildString(JsonElement child, string name, string error, out string? value)
  {
    value = null;
    if (!child.TryGetProperty(name, out JsonElement element))
    {
      return null;
    }

    if (element.ValueKind is not JsonValueKind.String)
    {
      return error;
    }

    value = element.GetString();
    return null;
  }

  /// <summary>Reads the child spec's optional workspaceRoot anchor: absent keeps null,
  ///     present must be a non-empty string (the given typed error otherwise).</summary>
  private static string? OptionalChildRoot(JsonElement child, string error, out string? value)
  {
    value = null;
    if (!child.TryGetProperty("workspaceRoot", out JsonElement element))
    {
      return null;
    }

    if (element.ValueKind is not JsonValueKind.String
        || element.GetString() is not { } parsed
        || parsed.Length == 0)
    {
      return error;
    }

    value = parsed;
    return null;
  }

  /// <summary>Reads the child spec's optional isolateInWorktree flag: absent keeps
  ///     false, present must be a JSON boolean (the given typed error otherwise).</summary>
  private static string? OptionalChildIsolate(JsonElement child, string error, out bool value)
  {
    value = false;
    if (!child.TryGetProperty("isolateInWorktree", out JsonElement element))
    {
      return null;
    }

    if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
    {
      return error;
    }

    value = element.GetBoolean();
    return null;
  }

  /// <summary>Strict route-argument parsing: non-empty name and text, urgency in range.</summary>
  private static string? RouteArgs(string json, out string? name, out string? text, out MessageUrgency urgency)
  {
    name = null;
    text = null;
    urgency = MessageUrgency.Normal;
    return RequireObject(json, out JsonElement root)
        ?? RejectUnknown(root, "name", "text", "urgency")
        ?? RequireNonEmpty(root, "name", out name)
        ?? RequireNonEmpty(root, "text", out text)
        ?? TryReadUrgency(root, out urgency);
  }

  /// <summary>Strict escalate-argument parsing: non-empty text, positive hops, urgency in range.</summary>
  private static string? EscalateArgs(string json, out string? text, out int hops, out MessageUrgency urgency)
  {
    text = null;
    hops = 1;
    urgency = MessageUrgency.Normal;
    return RequireObject(json, out JsonElement root)
        ?? RejectUnknown(root, "text", "hops", "urgency")
        ?? RequireNonEmpty(root, "text", out text)
        ?? ReadHops(root, out hops)
        ?? TryReadUrgency(root, out urgency);
  }

  /// <summary>Reads the optional hops member: absent keeps 1; present must be a
  ///     positive integer.</summary>
  private static string? ReadHops(JsonElement root, out int hops)
  {
    hops = 1;
    if (!root.TryGetProperty("hops", out JsonElement hopsElement))
    {
      return null;
    }

    if (hopsElement.ValueKind is not JsonValueKind.Number || !hopsElement.TryGetInt32(out int parsed) || parsed < 1)
    {
      return "'hops' must be a positive integer.";
    }

    hops = parsed;
    return null;
  }

  /// <summary>Ids cross into the domain strictly: a JSON object carrying exactly one "id"
  ///     member whose value is a Guid in "D" format. Anything else is a typed argument
  ///     error — never coerced, defaulted, or clamped.</summary>
  private static Result<AgentId> ParseIdArgument(string json)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException)
    {
      return Result.Failure<AgentId>(new DomainError("InvalidActionInput",
          "arguments must be a valid JSON object."));
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind is not JsonValueKind.Object)
      {
        return Result.Failure<AgentId>(new DomainError("InvalidActionInput",
            "arguments must be a JSON object."));
      }

      HashSet<string> allowed = new(StringComparer.Ordinal) { "id", "wakeOn", "timeoutSeconds" };
      string[] unknown = [.. doc.RootElement.EnumerateObject()
          .Where(p => !allowed.Contains(p.Name))
          .Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return Result.Failure<AgentId>(new DomainError("InvalidActionInput",
            $"unknown parameter(s): {string.Join(", ", unknown)}."));
      }

      if (!doc.RootElement.TryGetProperty("id", out JsonElement el)
          || el.ValueKind is not JsonValueKind.String
          || el.GetString() is not { } raw
          || !Guid.TryParseExact(raw, "D", out Guid guid))
      {
        return Result.Failure<AgentId>(new DomainError("InvalidArgument",
            "'id' must be a GUID string."));
      }

      AgentId agentId = new(guid);
      return Result.Success(agentId);
    }
  }

  private static (SpawnRequest? Request, string? DomainError) ParseArgs(string json)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(json);
    }
    catch (JsonException)
    {
      return (null, "arguments must be a valid JSON object.");
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind is not JsonValueKind.Object)
      {
        return (null, "arguments must be a JSON object.");
      }

      HashSet<string> allowed = new(StringComparer.Ordinal) { "taskPrompt", "model", "label", "grants", "workspaceRoot", "isolateInWorktree" };
      string[] unknown = [.. doc.RootElement.EnumerateObject()
          .Where(p => !allowed.Contains(p.Name))
          .Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return (null, $"unknown parameter(s): {string.Join(", ", unknown)}.");
      }

      if (!TryReadSpawnStrings(doc.RootElement, out string? taskPrompt, out string? model, out string? label, out string? workspaceRoot, out string? stringError))
      {
        return (null, stringError);
      }

      if (!TryReadIsolate(doc.RootElement, out bool isolateInWorktree, out string? isolateError))
      {
        return (null, isolateError);
      }

      if (!TryParseGrants(doc.RootElement, out SpawnContract? contract, out string? grantsError))
      {
        return (null, grantsError);
      }

      return (new SpawnRequest(taskPrompt!, string.IsNullOrEmpty(model) ? null : model,
          string.IsNullOrEmpty(label) ? null : label, Contract: contract,
          WorkspaceRoot: string.IsNullOrEmpty(workspaceRoot) ? null : workspaceRoot,
          IsolateInWorktree: isolateInWorktree), null);
    }
  }

  /// <summary>Reads the spawn arguments' string members in contract order:
  ///     taskPrompt required, then model, label, and workspaceRoot optional. The
  ///     first violation (with its verbatim message) stops the reads.</summary>
  private static bool TryReadSpawnStrings(JsonElement root, out string? taskPrompt, out string? model, out string? label, out string? workspaceRoot, out string? error)
  {
    taskPrompt = null;
    model = null;
    label = null;
    workspaceRoot = null;
    error = null;
    return TryGetString(root, "taskPrompt", required: true, out taskPrompt, out error)
        && TryGetString(root, "model", required: false, out model, out error)
        && TryGetString(root, "label", required: false, out label, out error)
        && TryGetString(root, "workspaceRoot", required: false, out workspaceRoot, out error);
  }

  /// <summary>Reads the optional isolateInWorktree member: absent keeps false;
  ///     present must be a JSON boolean.</summary>
  private static bool TryReadIsolate(JsonElement root, out bool value, out string? error)
  {
    value = false;
    error = null;
    if (!root.TryGetProperty("isolateInWorktree", out JsonElement isolateElement))
    {
      return true;
    }

    if (isolateElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
    {
      error = "isolateInWorktree must be a boolean.";
      return false;
    }

    value = isolateElement.GetBoolean();
    return true;
  }

  /// <summary>Parses the optional grants member into a SpawnContract: absent keeps
  ///     null; present must be an object whose tool.allow / tool.deny entries accept a
  ///     string or a string array — any other kind collapses to the empty string
  ///     (deny-by-default over an untyped world). False with the error set names the
  ///     violation; true carries the parsed contract (possibly null).</summary>
  private static bool TryParseGrants(JsonElement root, out SpawnContract? contract, out string? error)
  {
    contract = null;
    error = null;
    if (!root.TryGetProperty("grants", out JsonElement grantsElement))
    {
      return true;
    }

    if (grantsElement.ValueKind is not JsonValueKind.Object)
    {
      error = "'grants' must be an object with optional tool.allow / tool.deny entries.";
      return false;
    }

    Dictionary<string, string> parsed = [];
    foreach (string key in new[] { ToolGrantPolicy.AllowKey, ToolGrantPolicy.DenyKey })
    {
      if (!grantsElement.TryGetProperty(key, out JsonElement value))
      {
        continue;
      }

      parsed[key] = GrantValue(value);
    }

    contract = new SpawnContract(CapabilityGrants: parsed);
    return true;
  }

  /// <summary>One grants entry's value: a string verbatim, a string array joined
  ///     with ';' (non-string items dropped), any other kind the empty string
  ///     (deny-by-default over an untyped world).</summary>
  private static string GrantValue(JsonElement value)
    => value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" : NonStringGrant(value);

  /// <summary>The non-string grants kinds: only an array carries entries; every
  ///     other kind collapses to the empty string.</summary>
  private static string NonStringGrant(JsonElement value)
    => value.ValueKind is JsonValueKind.Array ? JoinedGrantItems(value) : string.Empty;

  /// <summary>Joins the array's string items with ';'; non-string items drop.</summary>
  private static string JoinedGrantItems(JsonElement value)
    => string.Join(";", value.EnumerateArray()
        .Where(item => item.ValueKind is JsonValueKind.String)
        .Select(item => item.GetString()));

  private static bool TryGetString(JsonElement root, string name, bool required, out string? value, out string? error)
  {
    value = null;
    error = null;
    if (!root.TryGetProperty(name, out JsonElement el))
    {
      if (required)
      {
        error = $"{name} is required and must be a non-empty string.";
      }

      return !required;
    }

    if (el.ValueKind is not JsonValueKind.String)
    {
      error = $"{name} must be a string.";
      return false;
    }

    string? s = el.GetString();
    if (string.IsNullOrWhiteSpace(s) && (required || s is not null))
    {
      error = $"{name} must be a non-empty string.";
      return false;
    }

    value = s;
    return true;
  }
}
