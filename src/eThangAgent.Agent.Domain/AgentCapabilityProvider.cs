using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The agent capability surface: spawn starts children as independent actors,
///     status and result retrieve their outcomes. Spawn renders only the running line —
///     reports arrive exclusively through the queries.</summary>
public sealed class AgentCapabilityProvider(
    IAgentSpawnCommand spawnCommand, IAgentQueries queries, Func<AgentRecord> parentContext) : ICapabilityProvider
{
  public const string ProviderId = "agent";

  private readonly IAgentSpawnCommand _spawnCommand = spawnCommand ?? throw new ArgumentNullException(nameof(spawnCommand));
  private readonly IAgentQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));
  private readonly Func<AgentRecord> _parentContext = parentContext ?? throw new ArgumentNullException(nameof(parentContext));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("spawn", "Spawn a child agent that runs autonomously in the background and returns immediately.",
            """
            Starts a child agent on a self-contained task and returns right away — never wait on the spawn call itself. Continue useful work or fan out siblings, then poll status and fetch result. Children may spawn their own children; depth limit is 3.
            Start failures return canonical error lines: InvalidSpawnRequest, DepthExceeded, MissingModel, ConcurrencyCapReached.
            Output contract:
            id=<id> status=running
            """,
            [
                new ActionParameter("taskPrompt", ActionParameterTypes.StringType, "Self-contained task for the child. State exactly what the report must contain."),
                new ActionParameter("model", ActionParameterTypes.StringType, "Optional provider model reference; omit to use the configured default."),
                new ActionParameter("label", ActionParameterTypes.StringType, "Optional free-text label for humans and logs."),
            ]),
        new("status", "Check whether a spawned child agent is still running, completed, or failed.",
            """
            Returns the child's current state as one annotation line. Poll between turns while other work continues.
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
            Returns the child's final report verbatim once it has finished. While it is still running you receive 'Error [NotComplete]' — check status again later. Unknown ids yield 'Error [NotFound]'. A failed child yields its partial report, or an Error [MaxIterations|Timeout|ProviderError] annotation when no report landed.
            """,
            [
                new ActionParameter("id", ActionParameterTypes.StringType, "GUID string of the child agent, exactly as returned by spawn."),
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
    _ => throw new InvalidOperationException($"Unknown agent status '{record.Status}' for agent '{record.Id}'."),
  };

  private static string ReasonText(AgentFailureReason? reason) => reason switch
  {
    AgentFailureReason.MaxIterations => "max-iterations",
    AgentFailureReason.Timeout => "timeout",
    AgentFailureReason.ProviderError => "provider-error",
    AgentFailureReason.Interrupted => "interrupted",
    _ => throw new InvalidOperationException($"Unknown agent failure reason '{reason}'."),
  };

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

      HashSet<string> allowed = new(StringComparer.Ordinal) { "taskPrompt", "model", "label" };
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

      return (new SpawnRequest(taskPrompt!, string.IsNullOrEmpty(model) ? null : model,
          string.IsNullOrEmpty(label) ? null : label), null);
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
