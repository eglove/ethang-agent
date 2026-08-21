using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateService : IStateService
{
    public const string HeadNs = "current";
    public const string HeadName = "head";
    public const string CertificateNs = "current";
    public const string CertificateName = "certificate";
    public const string GoalNs = "goal";
    public const string GoalName = "check";

    private readonly IStateStore _store;
    private readonly IEvidenceRunner _evidence;
    private readonly IWorkspaceContext _workspace;
    private readonly EvidenceOptions _options;

    public StateService(IStateStore store, IEvidenceRunner evidence,
        IWorkspaceContext workspace, EvidenceOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? EvidenceOptions.Default;
    }

    public async Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<string>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var kv = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        return kv is null
            ? Result<string>.Failure(new Error("KeyNotFound", $"'{key}' does not exist."))
            : Result<string>.Success(kv.Value);
    }

    public async Task<Result<StateKeyValue>> SetAsync(string key, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<StateKeyValue>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var saved = await _store.SetKeyCasAsync(_workspace.WorkspaceId, ns, name, value, expectedVersion, ct);
        if (saved is not null) return Result<StateKeyValue>.Success(saved);
        var current = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        return Result<StateKeyValue>.Failure(new Error("VersionConflict",
            $"Version conflict for '{key}': current version is {current?.Version ?? 0}."));
    }

    public async Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
    {
        var parsed = StateKey.Parse(key);
        if (!parsed.IsSuccess) return Result<string>.Failure(parsed.Error!);
        var (ns, name) = parsed.Value;
        var existing = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct);
        if (existing is null)
            return Result<string>.Failure(new Error("KeyNotFound", $"'{key}' does not exist."));
        var deleted = await _store.DeleteKeyCasAsync(_workspace.WorkspaceId, ns, name, expectedVersion, ct);
        return deleted
            ? Result<string>.Success($"deleted {key}")
            : Result<string>.Failure(new Error("VersionConflict",
                $"Version conflict for '{key}': current version is {existing.Version}."));
    }

    public async Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
    {
        var keys = await _store.ListKeysAsync(_workspace.WorkspaceId, ns, ct);
        return Result<IReadOnlyList<string>>.Success(
            keys.Select(k => $"{k.Ns}/{k.Name} v{k.Version}").ToList());
    }

    public async Task<Result<string>> TransitionAsync(string from, string to, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(summary))
            return Result<string>.Failure(new Error("InvalidTransition",
                "'from', 'to', and 'summary' are required."));
        var record = new TransitionRecord(
            $"tr-{Guid.NewGuid():N}", from, to, summary,
            evidence ?? [], "pending", DateTimeOffset.UtcNow);
        var stored = await _store.InsertTransitionAsync(_workspace.WorkspaceId, record, ct);
        await _store.AppendEventAsync(_workspace.WorkspaceId, "transition.attached",
            JsonSerializer.Serialize(new { id = stored.Id, from, to, summary }), ct);
        return Result<string>.Success(stored.Id);
    }

    public async Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
    {
        var workspaceId = _workspace.WorkspaceId;
        var selected = await _store.GetTransitionsAsync(workspaceId, ids ?? [], ct);
        var blocking = new List<string>();
        var results = new List<EvidenceResult>();

        if (ids is { Count: > 0 })
        {
            var found = selected.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids)
                if (!found.Contains(id))
                    blocking.Add($"Missing transition: {id}.");
        }
        else if (selected.Count == 0)
            blocking.Add("No transitions selected (none pending).");

        var head = await _store.GetKeyAsync(workspaceId, HeadNs, HeadName, ct);
        var headValue = head?.Value;
        var targetsHead = headValue is not null && selected.Any(t => t.To == headValue);

        foreach (var transition in selected)
        {
            if (transition.Evidence.Count == 0)
            {
                blocking.Add($"Transition {transition.Id} has no attached evidence.");
                results.Add(new EvidenceResult("(none)", false, "no evidence attached"));
                continue;
            }
            foreach (var command in transition.Evidence)
            {
                var result = await _evidence.RunAsync(command, ct);
                results.Add(result);
                if (!result.Confirmed)
                    blocking.Add($"Transition {transition.Id}: '{command}' — {result.Detail}");
            }
        }

        var certified = blocking.Count == 0;

        if (certified)
        {
            foreach (var transition in selected)
                await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "certified", ct);
            await _store.AppendEventAsync(workspaceId, "state.certified",
                JsonSerializer.Serialize(new { transitions = selected.Select(t => t.Id).ToArray() }), ct);
            if (targetsHead)
                await _store.SetKeyCasAsync(workspaceId, CertificateNs, CertificateName,
                    JsonSerializer.Serialize(new
                    {
                        transitions = selected.Select(t => t.Id).ToArray(),
                        certifiedAt = DateTimeOffset.UtcNow,
                    }), null, ct);
        }
        else
        {
            if (targetsHead)
                await _store.DeleteKeyCasAsync(workspaceId, CertificateNs, CertificateName, null, ct);
            foreach (var transition in selected)
                await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "violated", ct);
            await _store.AppendEventAsync(workspaceId, "state.violated",
                JsonSerializer.Serialize(new { reasons = blocking }), ct);
        }

        return new CertificationReport(certified, !certified, results, blocking);
    }

    public async Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
    {
        var goal = await _store.GetKeyAsync(_workspace.WorkspaceId, GoalNs, GoalName, ct);
        if (goal is null)
            return new CertificationReport(false, true, [], ["No goal/check commands stored."]);
        List<string> commands;
        try
        {
            commands = JsonSerializer.Deserialize<List<string>>(goal.Value) ?? [];
        }
        catch (JsonException)
        {
            return new CertificationReport(false, true, [],
                ["goal/check is not a valid JSON array of commands."]);
        }
        var results = new List<EvidenceResult>();
        var blocking = new List<string>();
        foreach (var command in commands)
        {
            var result = await _evidence.RunAsync(command, ct);
            results.Add(result);
            if (!result.Confirmed)
                blocking.Add($"'{command}' — {result.Detail}");
        }
        return new CertificationReport(blocking.Count == 0, blocking.Count > 0, results, blocking);
    }

    public async Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
    {
        var events = await _store.GetEventsAsync(_workspace.WorkspaceId, limit, ct);
        return Result<IReadOnlyList<string>>.Success(
            events.Select(e => $"{e.OccurredAt:u} {e.Kind} {e.PayloadJson}").ToList());
    }
}
