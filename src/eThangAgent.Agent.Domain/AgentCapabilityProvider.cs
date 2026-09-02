using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The agent capability surface: spawn starts children as independent actors,
///     status and result retrieve their outcomes. Spawn renders only the running line —
///     reports arrive exclusively through the queries.</summary>
public sealed class AgentCapabilityProvider(
    IAgentSpawnCommand spawnCommand, IAgentQueries queries, Func<AgentRecord> parentContext,
    IAgentRuntime? runtime = null, AgentLinkRegistry? links = null,
    Func<AgentRecord, SpawnRequest[], CancellationToken, Task<string>>? fanout = null,
    IAgentMailboxLocator? locator = null, Func<AgentId, IAgentEvents?>? eventsFor = null) : ICapabilityProvider
{
  public const string ProviderId = "agent";

  /// <summary>The action ids this provider exposes, without constructing it — the
  ///     grant-surface computation needs the names at composition time, where
  ///     resolving the provider itself would re-enter its singleton (R1).</summary>
  public static readonly string[] ActionNames =
      ["spawn", "status", "result", "wait", "send", "route", "escalate", "fanout"];

  private readonly IAgentSpawnCommand _spawnCommand = spawnCommand ?? throw new ArgumentNullException(nameof(spawnCommand));
  private readonly IAgentQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));
  private readonly Func<AgentRecord> _parentContext = parentContext ?? throw new ArgumentNullException(nameof(parentContext));
  private readonly IAgentRuntime? _runtime = runtime;
  private readonly AgentLinkRegistry? _links = links;
  private readonly Func<AgentRecord, SpawnRequest[], CancellationToken, Task<string>>? _fanout = fanout;
  private readonly IAgentMailboxLocator? _locator = locator;
  private readonly Func<AgentId, IAgentEvents?>? _eventsFor = eventsFor;

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("spawn", "Spawn a child agent that runs autonomously in the background and returns immediately.",
            """
            Starts a child agent on a self-contained task and returns right away — never wait on the spawn call itself. Continue useful work or fan out siblings; when you need a child's outcome, use agent.wait (one await) — status is a projection for humans, not a poll target. Children may spawn their own children; depth limit is 3.
            Start failures return canonical error lines: InvalidSpawnRequest, DepthExceeded, MissingModel, ConcurrencyCapReached.
            Output contract:
            id=<id> status=running
            """,
            [
                new ActionParameter("taskPrompt", ActionParameterTypes.StringType, "Self-contained task for the child. State exactly what the report must contain."),
                new ActionParameter("model", ActionParameterTypes.StringType, "Optional provider model reference; omit to use the configured default."),
                new ActionParameter("label", ActionParameterTypes.StringType, "Optional free-text label for humans and logs."),
                new ActionParameter("grants", ActionParameterTypes.StringType, "Optional capability grant: {\"tool.allow\": \"read; exec\", \"tool.deny\": \"web_fetch\"} (entries also accept string arrays). A granted child physically holds ONLY these tools plus agent actions; any other dispatch returns Error [GrantViolation] and is audited. Denying or omitting exec leaves the child no path to harness tools — grant exec unless the child needs none."),
            ]),
        new("status", "Projection of a spawned child agent's current state, for humans and debugging.",
            """
            Returns the child's current state as one annotation line. This is a projection, not a mechanism: when you need the outcome, use agent.wait (one await) instead of polling status.
            Output contract:
            id=<id> status=running
            id=<id> status=completed
            id=<id> status=failed reason=max-iterations|timeout|provider-error
            The reason suffix appears only when status=failed.
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
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
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
        new("fanout", "Spawn several children in one call and wait for ALL of them.",
            """
            Fan-out/fan-in: spawns every child described in 'children' (same shape as agent.spawn, one object each), waits for the whole set to settle, and joins. Per-member receipts name each child's id and terminal state; failed STARTS fail the join immediately; settled failures are collected and fail the join at the end. Receipts: <id>=COMPLETED|FAILED(reason).
            """,
            [
                new ActionParameter("label", ActionParameterTypes.StringType, "Optional label prefix for the graph."),
                new ActionParameter("children", ActionParameterTypes.StringType, "JSON array of child specs: [{\"taskPrompt\":\"...\",\"model\":\"...\",\"label\":\"...\"}] — taskPrompt required per child."),
            ]),
    ];

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    return actionName switch
    {
      "spawn" => await Spawn(jsonArguments, ct).ConfigureAwait(false),
      "status" => await Status(jsonArguments, ct).ConfigureAwait(false),
      "result" => await GetResult(jsonArguments, ct).ConfigureAwait(false),
      "wait" => await Wait(jsonArguments, ct).ConfigureAwait(false),
      "send" => Send(jsonArguments),
      "route" => Route(jsonArguments),
      "escalate" => await Escalate(jsonArguments, ct).ConfigureAwait(false),
      "fanout" => await Fanout(jsonArguments, ct).ConfigureAwait(false),
      _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
    };
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
    return started.IsSuccess
        ? CapabilityInvocationResult.Ok($"id={started.Value} status=running")
        : CapabilityInvocationResult.Fail($"Error [{started.Error.Code}]: {started.Error.Message}");
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

    Result<AgentRunOutcome> outcome = await _runtime.WhenSettledAsync(id.Value, ct).ConfigureAwait(false);
    if (!outcome.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{outcome.Error.Code}]: {outcome.Error.Message}");
    }

    AgentRunOutcome settled = outcome.Value;
    return settled.Status is AgentStatus.Completed || !string.IsNullOrEmpty(settled.Report)
        ? CapabilityInvocationResult.Ok(settled.Report)
        : CapabilityInvocationResult.Fail("Error [" + ReasonText(settled.Reason) + "]: the child settled without a usable report.");
  }
  /// <summary>agent.send: parent-to-child steering through the runtime's push-delivery.
  ///     Delivery to self is rejected at validation; unknown/finished targets surface
  ///     NotRunning; overflow surfaces MailboxFull — every failure has a recipient (A3).</summary>
  private CapabilityInvocationResult Send(string json)
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

    Result<bool> delivered = _runtime.Deliver(id.Value,
        new PendingMessage(text!, urgency, DateTimeOffset.UtcNow, SenderLabel(parent)));
    return delivered.IsSuccess
        ? CapabilityInvocationResult.Ok($"delivered to={id.Value} urgency={urgency.ToString().ToUpperInvariant()}")
        : CapabilityInvocationResult.Fail($"Error [{delivered.Error.Code}]: {delivered.Error.Message}");
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

    using (doc)
    {
      if (!doc.RootElement.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind is not JsonValueKind.String
          || !Guid.TryParseExact(idElement.GetString(), "D", out Guid parsed))
      {
        return "'id' must be a GUID string.";
      }

      id = new AgentId(parsed);
      if (!doc.RootElement.TryGetProperty("text", out JsonElement textElement)
          || textElement.ValueKind is not JsonValueKind.String
          || string.IsNullOrWhiteSpace(textElement.GetString()))
      {
        return "'text' must be a non-empty string.";
      }

      text = textElement.GetString();
      return doc.RootElement.TryGetProperty("urgency", out JsonElement urgencyElement)
          && (urgencyElement.ValueKind is not JsonValueKind.String
              || !Enum.TryParse(urgencyElement.GetString(), ignoreCase: true, out urgency))
          ? "'urgency' must be one of: normal, attention, urgent."
          : null;
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
    _ => throw new InvalidOperationException($"Unknown agent status '{record.Status}' for agent '{record.Id}'."),
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

    // Local half first (W3.1): the session's own runtime owns its children's mailboxes.
    Result<bool> delivered = _runtime.Deliver(targetId, message);
    if (!delivered.IsSuccess
        && MailboxErrors.NotRunning.Equals(delivered.Error.Code, StringComparison.Ordinal)
        && _locator?.TryGet(targetId) is { } foreign)
    {
      // Cross-container half (W3.2): the local runtime holds no mailbox for the target,
      // so the process-wide locator resolves the OWNING container's live mailbox. Delivery
      // and receipt contracts are identical to the local half; the audit event marks the
      // delivery cross-container on the target's stream (3.3).
      delivered = foreign.Deliver(message);
      if (delivered.IsSuccess)
      {
        // The audit event publishes on the OWNER's stream — the resolver returns the
        // target container's own event stream (3.3: the host-side audit trail reads it
        // where the target's host listens), never the sender's.
        _eventsFor?.Invoke(targetId)?.Publish(new MessageDeliveredEvent(targetId,
            DateTimeOffset.UtcNow, "cross-container", (int)urgency,
            System.Text.Encoding.UTF8.GetByteCount(text!)));
      }
    }

    return delivered.IsSuccess
        ? CapabilityInvocationResult.Ok($"delivered to={resolved.Value.AgentAddress} link={resolved.Value.Name}")
        : CapabilityInvocationResult.Fail($"Error [{delivered.Error.Code}]: {delivered.Error.Message}");
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
    _ = label; // label prefixing happens in the composition lambda via SpawnGraphRequest.Label
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

      HashSet<string> allowed = new(StringComparer.Ordinal) { "label", "children" };
      string[] unknown = [.. doc.RootElement.EnumerateObject().Where(p => !allowed.Contains(p.Name)).Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return $"unknown parameter(s): {string.Join(", ", unknown)}.";
      }

      if (!doc.RootElement.TryGetProperty("children", out JsonElement childrenElement) || childrenElement.ValueKind is not JsonValueKind.Array)
      {
        return "'children' must be an array of child specs.";
      }

      label = doc.RootElement.TryGetProperty("label", out JsonElement labelElement) && labelElement.ValueKind is JsonValueKind.String
          ? labelElement.GetString()
          : null;

      List<SpawnRequest> parsed = [];
      int index = 0;
      foreach (JsonElement child in childrenElement.EnumerateArray())
      {
        index++;
        if (child.ValueKind is not JsonValueKind.Object)
        {
          return $"children[{index}] must be an object.";
        }

        if (!child.TryGetProperty("taskPrompt", out JsonElement promptElement) || promptElement.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(promptElement.GetString()))
        {
          return $"children[{index}].taskPrompt must be a non-empty string.";
        }

        string? model = child.TryGetProperty("model", out JsonElement modelElement) && modelElement.ValueKind is JsonValueKind.String ? modelElement.GetString() : null;
        string? childLabel = child.TryGetProperty("label", out JsonElement childLabelElement) && childLabelElement.ValueKind is JsonValueKind.String ? childLabelElement.GetString() : null;
        parsed.Add(new SpawnRequest(promptElement.GetString()!, model, childLabel));
      }

      children = parsed;
      return null;
    }
  }

  /// <summary>Strict route-argument parsing: non-empty name and text, urgency in range.</summary>
  private static string? RouteArgs(string json, out string? name, out string? text, out MessageUrgency urgency)
  {
    name = null;
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

    using (doc)
    {
      if (doc.RootElement.ValueKind is not JsonValueKind.Object)
      {
        return "arguments must be a JSON object.";
      }

      HashSet<string> allowed = new(StringComparer.Ordinal) { "name", "text", "urgency" };
      string[] unknown = [.. doc.RootElement.EnumerateObject().Where(p => !allowed.Contains(p.Name)).Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return $"unknown parameter(s): {string.Join(", ", unknown)}.";
      }

      if (!doc.RootElement.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
      {
        return "'name' must be a non-empty string.";
      }

      name = nameElement.GetString();
      if (!doc.RootElement.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(textElement.GetString()))
      {
        return "'text' must be a non-empty string.";
      }

      text = textElement.GetString();
      return doc.RootElement.TryGetProperty("urgency", out JsonElement urgencyElement)
          && (urgencyElement.ValueKind is not JsonValueKind.String
              || !Enum.TryParse(urgencyElement.GetString(), ignoreCase: true, out urgency))
          ? "'urgency' must be one of: normal, attention, urgent."
          : null;
    }
  }

  /// <summary>Strict escalate-argument parsing: non-empty text, positive hops, urgency in range.</summary>
  private static string? EscalateArgs(string json, out string? text, out int hops, out MessageUrgency urgency)
  {
    text = null;
    hops = 1;
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

    using (doc)
    {
      if (doc.RootElement.ValueKind is not JsonValueKind.Object)
      {
        return "arguments must be a JSON object.";
      }

      HashSet<string> allowed = new(StringComparer.Ordinal) { "text", "hops", "urgency" };
      string[] unknown = [.. doc.RootElement.EnumerateObject().Where(p => !allowed.Contains(p.Name)).Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return $"unknown parameter(s): {string.Join(", ", unknown)}.";
      }

      if (!doc.RootElement.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(textElement.GetString()))
      {
        return "'text' must be a non-empty string.";
      }

      text = textElement.GetString();
      if (doc.RootElement.TryGetProperty("hops", out JsonElement hopsElement)
          && (hopsElement.ValueKind is not JsonValueKind.Number || !hopsElement.TryGetInt32(out int parsed) || parsed < 1))
      {
        return "'hops' must be a positive integer.";
      }
      else if (doc.RootElement.TryGetProperty("hops", out hopsElement))
      {
        hops = hopsElement.GetInt32();
      }

      return doc.RootElement.TryGetProperty("urgency", out JsonElement urgencyElement)
          && (urgencyElement.ValueKind is not JsonValueKind.String
              || !Enum.TryParse(urgencyElement.GetString(), ignoreCase: true, out urgency))
          ? "'urgency' must be one of: normal, attention, urgent."
          : null;
    }
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

      HashSet<string> allowed = new(StringComparer.Ordinal) { "id" };
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

      HashSet<string> allowed = new(StringComparer.Ordinal) { "taskPrompt", "model", "label", "grants" };
      string[] unknown = [.. doc.RootElement.EnumerateObject()
          .Where(p => !allowed.Contains(p.Name))
          .Select(p => p.Name)];
      if (unknown.Length > 0)
      {
        return (null, $"unknown parameter(s): {string.Join(", ", unknown)}.");
      }

      if (!TryGetString(doc.RootElement, "taskPrompt", required: true, out string? taskPrompt, out string? tpError))
      {
        return (null, tpError);
      }

      if (!TryGetString(doc.RootElement, "model", required: false, out string? model, out string? modelError))
      {
        return (null, modelError);
      }

      if (!TryGetString(doc.RootElement, "label", required: false, out string? label, out string? labelError))
      {
        return (null, labelError);
      }

      SpawnContract? contract = null;
      if (doc.RootElement.TryGetProperty("grants", out JsonElement grantsElement))
      {
        if (grantsElement.ValueKind is not JsonValueKind.Object)
        {
          return (null, "'grants' must be an object with optional tool.allow / tool.deny entries.");
        }

        Dictionary<string, string> parsed = [];
        foreach (string key in new[] { ToolGrantPolicy.AllowKey, ToolGrantPolicy.DenyKey })
        {
          if (!grantsElement.TryGetProperty(key, out JsonElement value))
          {
            continue;
          }

          parsed[key] = value.ValueKind switch
          {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join(";", value.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString())),
            JsonValueKind.Number => string.Empty,
            JsonValueKind.True => string.Empty,
            JsonValueKind.False => string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Object => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => string.Empty,
          };
        }

        contract = new SpawnContract(CapabilityGrants: parsed);
      }

      return (new SpawnRequest(taskPrompt!, string.IsNullOrEmpty(model) ? null : model,
          string.IsNullOrEmpty(label) ? null : label, Contract: contract), null);
    }
  }

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
