using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

public sealed class AgentCapabilityProvider(ISubAgentSpawner spawner, Func<AgentRecord> parentContext) : ICapabilityProvider
{
    public const string ProviderId = "agent";

    private const int ReportOverflowChars = 50_000;

    private readonly ISubAgentSpawner _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    private readonly Func<AgentRecord> _parentContext = parentContext ?? throw new ArgumentNullException(nameof(parentContext));

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("spawn", "Spawn a child agent that runs autonomously and returns its final report.",
            """
            Runs a child agent to completion on a self-contained task and returns its report.
            Failures keep the same shape with status=failed and a reason: max-iterations, timeout, provider-error, depth-exceeded, or missing-model. Children may spawn their own children; depth limit is 3.
            Output contract:
            [agent] id=<id> status=completed depth=1 model=<model> label=<label>
            --- report ---
            <child's final report text>
            --- end report ---
            """,
            [
                new ActionParameter("taskPrompt", "String", "Self-contained task for the child. State exactly what the report must contain."),
                new ActionParameter("model", "String", "Optional provider model reference; omit to use the configured default."),
                new ActionParameter("label", "String", "Optional free-text label for humans and logs."),
            ]),
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        return actionName switch
        {
            "spawn" => await SpawnAsync(jsonArguments, ct),
            _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
        };
    }

    private async Task<CapabilityInvocationResult> SpawnAsync(string json, CancellationToken ct)
    {
        var (request, parseError) = ParseArgs(json);
        if (parseError is not null)
            return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {parseError}");

        var outcome = await _spawner.SpawnAsync(_parentContext(), request!, ct);
        return CapabilityInvocationResult.Ok(Render(outcome, request!.Label));
    }

    private static (SpawnRequest? Request, string? Error) ParseArgs(string json)
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
                return (null, "arguments must be a JSON object.");

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "taskPrompt", "model", "label" };
            var unknown = doc.RootElement.EnumerateObject()
                .Where(p => !allowed.Contains(p.Name))
                .Select(p => p.Name)
                .ToArray();
            if (unknown.Length > 0)
                return (null, $"unknown parameter(s): {string.Join(", ", unknown)}.");

            if (!TryGetString(doc.RootElement, "taskPrompt", required: true, out var taskPrompt, out var tpError))
                return (null, tpError);
            if (!TryGetString(doc.RootElement, "model", required: false, out var model, out var modelError))
                return (null, modelError);
            if (!TryGetString(doc.RootElement, "label", required: false, out var label, out var labelError))
                return (null, labelError);

            return (new SpawnRequest(taskPrompt!, string.IsNullOrEmpty(model) ? null : model,
                string.IsNullOrEmpty(label) ? null : label), null);
        }
    }

    private static bool TryGetString(JsonElement root, string name, bool required, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!root.TryGetProperty(name, out var el))
        {
            if (required)
                error = $"{name} is required and must be a non-empty string.";
            return !required;
        }

        if (el.ValueKind is not JsonValueKind.String)
        {
            error = $"{name} must be a string.";
            return false;
        }

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            if (required || s is not null)
            {
                error = $"{name} must be a non-empty string.";
                return false;
            }
        }

        value = s;
        return true;
    }

    private static string Render(Result<AgentRunOutcome> outcome, string? label)
    {
        if (!outcome.IsSuccess)
        {
            var failReason = outcome.Error!.Code switch
            {
                "DepthExceeded" => "depth-exceeded",
                "MissingModel" => "missing-model",
                _ => "provider-error",
            };
            return $"[agent] status=failed reason={failReason}\n--- report ---\n{outcome.Error.Message}\n--- end report ---";
        }

        var o = outcome.Value!;
        var status = o.Status is AgentStatus.Completed ? "completed" : "failed";
        var headerLabel = label is null ? "" : $" label={label}";
        var reason = o.Reason is null ? null : o.Reason switch
        {
            AgentFailureReason.MaxIterations => "max-iterations",
            AgentFailureReason.Timeout => "timeout",
            AgentFailureReason.ProviderError => "provider-error",
            _ => "provider-error",
        };
        var header = $"[agent] id={o.ChildId} status={status}{(reason is null ? "" : $" reason={reason}")} depth={o.Depth} model={o.ModelUsed}{headerLabel}";
        var report = o.Report ?? string.Empty;
        if (report.Length > ReportOverflowChars)
            report += $"\n[agent] note: report exceeded {ReportOverflowChars} characters; full text stored with the agent record.";
        return $"{header}\n--- report ---\n{report}\n--- end report ---";
    }
}