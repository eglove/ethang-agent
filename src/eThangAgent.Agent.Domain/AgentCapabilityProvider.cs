using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The agent capability surface: status and result retrieve an agent's state and
///     report, resume continues a settled one, cancel stops a running one, and launch starts
///     a SIBLING one (issue #338).
///     <para>LAUNCH IS NOT DELEGATION. The agent it starts is recorded as nobody's: nothing is
///     written that could be read back as a hierarchy - no owner field, no ancestor reference,
///     and no nesting fact. It is placed by its own workspace, appears as a cell beside the
///     caller's, and is followed afterwards with agent.status and agent.result - the caller's own
///     later acts. That is what makes this the act #334 retired agent.spawn for, rather than that
///     act restored.</para></summary>
public sealed class AgentCapabilityProvider : ICapabilityProvider
{
  public const string ProviderId = "agent";

  /// <summary>The action ids this provider exposes, without constructing it — the
  ///     grant-surface computation needs the names at composition time, where
  ///     resolving the provider itself would re-enter its singleton (R1).</summary>
  public static readonly string[] ActionNames =
      ["status", "result", "resume", "cancel", "launch"];

  /// <summary>Builds the dispatch map eagerly: every handler closes over this
  ///     provider's collaborators, so the map is per provider (one per session).</summary>
  public AgentCapabilityProvider(AgentProviderServices services)
  {
    ArgumentNullException.ThrowIfNull(services);
    _queries = services.Queries ?? throw new ArgumentNullException(nameof(services), "Queries must not be null.");
    _runtime = services.Runtime;
    _clock = services.Clock;
    _progress = services.Progress;
    _launcher = services.Launcher;
    _handlers = BuildHandlers();
  }

  private readonly IAgentQueries _queries;
  private readonly IAgentRuntime? _runtime;
  private readonly TimeProvider? _clock;
  private readonly AgentProgressLedger? _progress;
  private readonly IAgentLaunchCommand? _launcher;
  private readonly FrozenDictionary<string, ActionHandler> _handlers;

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("status", "Projection of an agent's current state, for humans and debugging.",
            """
            Returns the agent's current state as one annotation line. This is a projection, not a mechanism: the outcome of work is read from the record once it is terminal — agent.result for an agent's report, and the chain record for whether a step was done.
            Output contract:
            id=<id> status=running
            id=<id> status=queued
            id=<id> status=completed
            id=<id> status=failed reason=max-iterations|timeout|provider-error|interrupted|hung|budget-exhausted|invalid-result
            id=<id> status=interrupted
            status=queued means the agent is parked at the runtime's concurrency limit: it has no run yet and starts on its own when a slot frees. The reason suffix appears only when status=failed.
            A RUNNING agent's line also carries its latest mid-run activity:
            id=<id> status=running progress phase=model-call|tool-exec|draining label=<short tag> age=<n>s
            phase/label name what the agent is doing right now (label is a phase tag such as 'iteration' or 'tool:read', never content) and age is how long since that beat. The suffix appears only while running and only when a beat has been observed.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the agent."),
            ]),
      new("result", "Fetch the final report of an agent.",
            """
            Returns the agent's final report verbatim once it has finished. While it is still running you receive 'Error [NotComplete]' — read it again once the record is terminal. Unknown ids yield 'Error [NotFound]'. A failed agent yields its partial report, or an Error [MaxIterations|Timeout|ProviderError] annotation when no report landed.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the agent."),
            ]),
      new("resume", "Resume a settled agent with a new message (same id, same context).",
            """
            Continues a SETTLED agent (completed or failed) on the SAME id: its persisted transcript,
            workspace anchor, and grants carry over; 'text' becomes the agent's next instruction.
            Returns id=<id> status=running — read the fresh outcome with agent.result once it
            settles. Fails: NotRunning (unknown id, or the agent is still running — a running
            agent is steered by the human in its own cell, never by a tool call), InvalidMessage (empty text).
            Every fix round on a settled agent beats starting a new one from scratch.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the settled agent."),
                new ActionParameter("text", ActionParameterTypes.StringType, "The continuation instruction for the agent. Non-empty."),
            ]),
      new("cancel", "Interrupt a running agent by id (the model-facing stop).",
            """
            Cancels one RUNNING agent mid-run: the run's cancellation token fires, the loop repairs any half-finished tool batch, and the agent settles as interrupted - its partial transcript stays retrievable with agent.result, and agent.resume can restart it. A QUEUED agent (parked at the concurrency limit, no run yet) is removed from the queue and settles as failed reason=interrupted. Routed through the runtime's explicit-interrupt path (one of the three sanctioned cancellation sources). Fails to you: NotRunning (unknown id, or the agent already settled - nothing to cancel), NotAvailable (no runtime wired).
            Output contract:
            interrupted id=<id>
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the agent."),
            ]),
       new("launch", "Start a new agent in your own workspace, as a sibling of you.",
             """
             Starts one agent: a session of its own, in YOUR workspace, seeded with 'prompt' as its first turn, and shown as a cell beside yours. Returns its id at once and returns - this is not a delegation and not a join.
             The agent it starts is a SIBLING, not a subordinate. Nothing records it as yours: it carries no owner, no ancestor reference, and no nesting fact of any kind, and it is indistinguishable from an agent the user opened by hand apart from its creation time, its label, and its prompt. There is no fan-out, and nothing caps an agent by nesting.
             Optional 'label' is the caption its cell shows (default: the workspace name). Optional 'model' serves that agent; omit it to inherit this workspace's own choice.
             HOW TO FOLLOW IT: agent.status projects its current state and agent.result returns its report once it settles. Both are your own later acts; nothing joins on the agent you started. agent.resume addresses a run inside THIS host and cannot address the host a launched agent runs in, so it is not available on the id this action returns.
             Output contract:
             id=<id> status=running
             Fails: InvalidActionInput (empty 'prompt'), LaunchUnavailable (this host wired no session-start seam), and the app's own typed refusal verbatim (SessionRequestUnreachable when no live turn can carry the request - never a hang).
             """,
             [
                 new ActionParameter("prompt", ActionParameterTypes.StringType, "Required. The new agent's first turn. Non-empty."),
                 new ActionParameter("label", ActionParameterTypes.StringType, "Optional. The caption its cell shows. Defaults to the workspace name."),
                 new ActionParameter("model", ActionParameterTypes.StringType, "Optional. The model to serve the new agent; omit to inherit this workspace's choice."),
             ],
             ["prompt"]),
  ];

  private delegate Task<CapabilityInvocationResult> ActionHandler(string json, CancellationToken ct);

  private FrozenDictionary<string, ActionHandler> BuildHandlers()
  {
    return new Dictionary<string, ActionHandler>(StringComparer.Ordinal)
    {
      ["status"] = Status,
      ["result"] = GetResult,
      ["resume"] = (json, _) => Resume(json),
      ["cancel"] = Cancel,
      ["launch"] = Launch,
    }.ToFrozenDictionary();
  }

  public Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    return _handlers.TryGetValue(actionName, out ActionHandler? handler)
        ? handler(jsonArguments, ct)
        : Task.FromResult(CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."));
  }

  /// <summary>agent.resume: same-id fix-round continuation of a SETTLED child. Reuses
  ///     resume-argument parsing (id + text; no urgency). Every failure has a recipient:
  ///     NotRunning (unknown/still-running/foreign), InvalidMessage, and ResumeUnsupported
  ///     on remote runtimes.</summary>
  private async Task<CapabilityInvocationResult> Resume(string json)
  {
    if (_runtime is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [NotAvailable]: agent.resume needs a runtime wired into this session's capability provider.");
    }

    string? parseError = ResumeArgs(json, out AgentId? id, out string? text);
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

  private async Task<CapabilityInvocationResult> Status(string json, CancellationToken ct)
  {
    Result<AgentId> id = ParseIdArgument(json);
    if (!id.IsSuccess)
    {
      return CapabilityInvocationResult.Fail($"Error [{id.Error.Code}]: {id.Error.Message}");
    }

    Result<AgentRecord> lookup = await _queries.GetStatus(id.Value, ct).ConfigureAwait(false);
    return lookup.IsSuccess
        ? CapabilityInvocationResult.Ok(StateLine(lookup.Value) + ProgressSuffix(lookup.Value))
        : CapabilityInvocationResult.Fail($"Error [{lookup.Error.Code}]: {lookup.Error.Message}");
  }

  /// <summary>The mid-run activity suffix for a RUNNING child (issue #93): the phase and
  ///     label of its latest progress beat plus the age of that beat, appended to the
  ///     status line so a parent watching a long run learns what the child is DOING
  ///     instead of only that it is running. Empty for every other status (a terminal
  ///     record has no current phase) and empty when no ledger is wired or the ledger
  ///     holds no beat for this child — legacy wiring stays byte-identical.</summary>
  private string ProgressSuffix(AgentRecord record)
  {
    if (_progress is null || record.Status is not AgentStatus.Running
        || _progress.Find(record.Id) is not { } snapshot)
    {
      return "";
    }

    TimeSpan age = (_clock ?? TimeProvider.System).GetUtcNow() - snapshot.At;
    int seconds = Math.Max((int)age.TotalSeconds, 0);
    return string.Create(CultureInfo.InvariantCulture,
        $" progress phase={PhaseWord(snapshot.Phase)} label={snapshot.Label} age={seconds}s");
  }

  /// <summary>Lowercase phase words for the status line (CA1308 forbids ToLowerInvariant
  ///     normalization; the words are pinned, not computed).</summary>
  private static string PhaseWord(AgentPhase phase) => phase switch
  {
    AgentPhase.ModelCall => "model-call",
    AgentPhase.ToolExec => "tool-exec",
    AgentPhase.Draining => "draining",
    _ => throw new InvalidOperationException($"Unknown child phase '{phase}'."),
  };

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

  /// <summary>Strict resume-argument parsing: 'id' (Guid D) and a non-empty 'text',
  ///     with no other members accepted. A resume is not a message, so it owns its own
  ///     gate rather than inheriting a message-shaped one that no longer has a caller.</summary>
  private static string? ResumeArgs(string json, out AgentId? id, out string? text)
  {
    id = null;
    text = null;
    if (RequireObject(json, out JsonElement root) is { } objectError)
    {
      return objectError;
    }

    if (RejectUnknown(root, "id", "text") is { } unknownError)
    {
      return unknownError;
    }

    if (!root.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind is not JsonValueKind.String
        || !Guid.TryParseExact(idElement.GetString(), "D", out Guid parsed))
    {
      return "'id' must be a GUID string.";
    }

    id = new AgentId(parsed);
    if (!root.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind is not JsonValueKind.String
        || string.IsNullOrWhiteSpace(textElement.GetString()))
    {
      return "'text' must be a non-empty string.";
    }

    text = textElement.GetString();
    return null;
  }

  /// <summary>agent.launch (issue #338): start a SIBLING agent and return its id.
  ///     <para>IT RETURNS AND STOPS THERE. Nothing here awaits the new agent, holds a
  ///     reference to its outcome, or records who asked: the caller reads the outcome with
  ///     agent.result, which is its own later act. A launch that joined would be
  ///     supervision by another name.</para>
  ///     <para>EVERY FAILURE HAS A RECIPIENT: InvalidActionInput for an argument the caller
  ///     can correct, LaunchUnavailable when this host wired no launch seam (a test rig, a
  ///     headless container), and the port's own typed code verbatim - including
  ///     SessionRequestUnreachable when no live turn can carry the request, which fails
  ///     immediately rather than hanging.</para></summary>
  private async Task<CapabilityInvocationResult> Launch(string json, CancellationToken ct)
  {
    if (_launcher is null)
    {
      return CapabilityInvocationResult.Fail(
          "Error [LaunchUnavailable]: agent.launch needs a session-start seam wired into this session's capability provider.");
    }

    string? parseError = LaunchArgs(json, out AgentLaunchRequest? request);
    if (parseError is not null)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");
    }

    Result<AgentId> launched = await _launcher.StartAsync(request!, ct).ConfigureAwait(false);
    return launched.IsSuccess
        ? CapabilityInvocationResult.Ok($"id={launched.Value} status=running")
        : CapabilityInvocationResult.Fail($"Error [{launched.Error.Code}]: {launched.Error.Message}");
  }

  /// <summary>Strict launch-argument parsing: a non-empty 'prompt' plus optional 'label'
  ///     and 'model', with no other members accepted. Every member is read before any of
  ///     them is acted on, so a refusal means nothing was started.</summary>
  private static string? LaunchArgs(string json, out AgentLaunchRequest? request)
  {
    request = null;
    return RequireObject(json, out JsonElement root) is { } objectError
        ? objectError
        : LaunchObjectArgs(root, out request);
  }

  /// <summary>The object-shape gates of a launch: no unknown members, then the request
  ///     itself. Both halves stay expression-shaped so the whole parse is one decision path.
  ///     </summary>
  private static string? LaunchObjectArgs(JsonElement root, out AgentLaunchRequest? request)
  {
    request = null;
    return RejectUnknown(root, "prompt", "label", "model") is { } unknownError
        ? unknownError
        : LaunchRequestOf(root, out request);
  }

  /// <summary>Reads the launch request's members: 'prompt' is required and non-empty,
  ///     'label' and 'model' are optional strings. Split from the object gate above so each
  ///     half stays a short sequence of checks rather than one branch chain (S1541).</summary>
  private static string? LaunchRequestOf(JsonElement root, out AgentLaunchRequest? request)
  {
    request = null;
    if (!root.TryGetProperty("prompt", out JsonElement promptElement)
        || promptElement.ValueKind is not JsonValueKind.String
        || promptElement.GetString() is not { } prompt
        || string.IsNullOrWhiteSpace(prompt))
    {
      return "'prompt' must be a non-empty string.";
    }

    if (OptionalString(root, "label") is { } labelError)
    {
      return labelError;
    }

    if (OptionalString(root, "model") is { } modelError)
    {
      return modelError;
    }

    request = new AgentLaunchRequest(prompt,
        OptionalValue(root, "label"), OptionalValue(root, "model"));
    return null;
  }

  /// <summary>An optional string member: absent is fine, present-but-not-a-string is not.
  ///     A present-but-empty value is accepted as 'the caller stated nothing', which is the
  ///     same thing absence means for these two - neither is load-bearing.</summary>
  private static string? OptionalString(JsonElement root, string member)
    => root.TryGetProperty(member, out JsonElement value) && value.ValueKind is not JsonValueKind.String
        ? $"'{member}' must be a string."
        : null;

  /// <summary>An optional string member's value, or null when it was absent.</summary>
  private static string? OptionalValue(JsonElement root, string member)
    => root.TryGetProperty(member, out JsonElement value) ? value.GetString() : null;

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
}
