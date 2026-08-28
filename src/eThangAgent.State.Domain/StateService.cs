using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateService(IStateStore store, IEvidenceRunner evidence,
    IWorkspaceContext workspace) : IStateService
{
  public const string HeadNs = "current";
  public const string HeadName = "head";
  public const string CertificateNs = "current";
  public const string CertificateName = "certificate";
  public const string GoalNs = "goal";
  public const string GoalName = "check";
  private const string CurrentPrefix = "current";
  private const string VersionConflict = "VersionConflict";

  private readonly IStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IEvidenceRunner _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  public async Task<Result<string>> GetAsync(string key, CancellationToken ct = default)
  {
    Result<(string Ns, string Name)> parsed = StateKey.Parse(key);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<string>(parsed.Error);
    }

    (string? ns, string? name) = parsed.Value;
    StateKeyValue? kv = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct).ConfigureAwait(false);
    return kv is null
        ? Result.Failure<string>(new DomainError("KeyNotFound", $"'{key}' does not exist."))
        : Result.Success(kv.Value);
  }

  public async Task<Result<StateKeyValue>> SetAsync(string key, string value,
      int? expectedVersion, CancellationToken ct = default)
  {
    Result<(string Ns, string Name)> parsed = StateKey.Parse(key);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<StateKeyValue>(parsed.Error);
    }

    (string? ns, string? name) = parsed.Value;
    StateKeyValue? saved = await _store.SetKeyCasAsync(_workspace.WorkspaceId, ns, name, value, expectedVersion, ct).ConfigureAwait(false);
    if (saved is not null)
    {
      return Result.Success(saved);
    }

    StateKeyValue? current = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct).ConfigureAwait(false);
    return Result.Failure<StateKeyValue>(new DomainError(VersionConflict,
        $"Version conflict for '{key}': current version is {current?.Version ?? 0}."));
  }

  public async Task<Result<string>> DeleteAsync(string key, int? expectedVersion, CancellationToken ct = default)
  {
    Result<(string Ns, string Name)> parsed = StateKey.Parse(key);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<string>(parsed.Error);
    }

    (string? ns, string? name) = parsed.Value;
    StateKeyValue? existing = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct).ConfigureAwait(false);
    if (existing is null)
    {
      return Result.Failure<string>(new DomainError("KeyNotFound", $"'{key}' does not exist."));
    }

    bool deleted = await _store.DeleteKeyCasAsync(_workspace.WorkspaceId, ns, name, expectedVersion, ct).ConfigureAwait(false);
    return deleted
        ? Result.Success($"deleted {key}")
        : Result.Failure<string>(new DomainError(VersionConflict,
            $"Version conflict for '{key}': current version is {existing.Version}."));
  }

  public async Task<Result<IReadOnlyList<string>>> ListAsync(string? ns, CancellationToken ct = default)
  {
    IReadOnlyList<StateKeyValue> keys = await _store.ListKeysAsync(_workspace.WorkspaceId, ns, ct).ConfigureAwait(false);
    return Result.Success<IReadOnlyList<string>>(
        [.. keys.Select(k => $"{k.Ns}/{k.Name} v{k.Version}")]);
  }

  public async Task<Result<StateKeyValue>> AppendAsync(string key, string text,
      int? expectedVersion, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(text) || text.Trim() != text || text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
    {
      return Result.Failure<StateKeyValue>(new DomainError("InvalidText",
                "Append text must be a single line without leading or trailing whitespace."));
    }

    // Read the stored row for its version (GetAsync returns only the value).
    Result<(string Ns, string Name)> parsedKey = StateKey.Parse(key);
    if (!parsedKey.IsSuccess)
    {
      return Result.Failure<StateKeyValue>(parsedKey.Error);
    }

    (string? ns, string? name) = parsedKey.Value;
    StateKeyValue? row = await _store.GetKeyAsync(_workspace.WorkspaceId, ns, name, ct).ConfigureAwait(false);

    if (row is null)
    {
      if (expectedVersion.HasValue)
      {
        return Result.Failure<StateKeyValue>(new DomainError(VersionConflict,
              $"Version conflict for '{key}': it does not exist (expected version {expectedVersion.Value})."));
      }

      Result<StateKeyValue> created = await SetAsync(key, text, null, ct).ConfigureAwait(false);
      return created;
    }

    if (expectedVersion.HasValue && expectedVersion.Value != row.Version)
    {
      return Result.Failure<StateKeyValue>(new DomainError(VersionConflict,
            $"Version conflict for '{key}': current version is {row.Version}."));
    }

    string appended = row.Value + "\n" + text;
    return await SetAsync(key, appended, row.Version, ct).ConfigureAwait(false);
  }

  public async Task<Result<int>> DeletePrefixAsync(string nsPrefix, CancellationToken ct = default)
  {
    return string.IsNullOrWhiteSpace(nsPrefix) || nsPrefix.Trim() != nsPrefix
        || nsPrefix.Contains('/', StringComparison.Ordinal) || nsPrefix.Any(char.IsWhiteSpace)
      ? Result.Failure<int>(new DomainError("InvalidKey",
                "Namespace prefix must be a legal namespace segment: non-empty, whitespace-free, no slash."))
      : await ValidateAndDelete(nsPrefix, ct).ConfigureAwait(false);
  }

  private async Task<Result<int>> ValidateAndDelete(string nsPrefix, CancellationToken ct)
  {
    if (nsPrefix == "todo" || nsPrefix.StartsWith("todo.", StringComparison.Ordinal))
    {
      return Result.Failure<int>(new DomainError("ReservedNamespace",
            "'todo' namespaces are owned by the todo tool and cannot be bulk-deleted."));
    }

    if (nsPrefix == CurrentPrefix)
    {
      return Result.Failure<int>(new DomainError("ReservedNamespace",
            $"'{CurrentPrefix}' namespaces carry head/certificate state and cannot be bulk-deleted."));
    }

    int deleted = await _store.DeleteNamespacePrefixAsync(_workspace.WorkspaceId, nsPrefix, ct).ConfigureAwait(false);
    return Result.Success(deleted);
  }
  public async Task<Result<IReadOnlyList<StateSearchHit>>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(query))
    {
      return Result.Failure<IReadOnlyList<StateSearchHit>>(
            new DomainError("InvalidQuery", "Query is required and must contain non-whitespace characters."));
    }

    if (limit is < 1 or > 100)
    {
      return Result.Failure<IReadOnlyList<StateSearchHit>>(
            new DomainError("InvalidLimit", $"Limit must be between 1 and 100, got {limit}."));
    }

    Result<IReadOnlyList<StateSearchHit>> hits =
        await _store.SearchKeysAsync(_workspace.WorkspaceId, query.Trim(), limit, ct).ConfigureAwait(false);
    return hits;
  }
  public async Task<Result<string>> TransitionAsync(string from, string toState, string summary,
        IReadOnlyList<string> evidence, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(toState) || string.IsNullOrWhiteSpace(summary))
    {
      return Result.Failure<string>(new DomainError("InvalidTransition",
                "'from', 'to', and 'summary' are required."));
    }

    TransitionRecord record = new(
            $"tr-{Guid.NewGuid():N}", from, toState, summary,
            evidence ?? [], "pending", DateTimeOffset.UtcNow);
    TransitionRecord stored = await _store.InsertTransitionAsync(_workspace.WorkspaceId, record, ct).ConfigureAwait(false);
    await _store.AppendEventAsync(_workspace.WorkspaceId, "transition.attached",
        JsonSerializer.Serialize(new { id = stored.Id, from, toState, summary }), ct).ConfigureAwait(false);
    return Result.Success(stored.Id);
  }

  public async Task<CertificationReport> VerifyAsync(IReadOnlyList<string>? ids, CancellationToken ct = default)
  {
    string workspaceId = _workspace.WorkspaceId;
    IReadOnlyList<TransitionRecord> selected = await _store.GetTransitionsAsync(workspaceId, ids ?? [], ct).ConfigureAwait(false);
    List<string> blocking = [];
    List<EvidenceResult> results = [];
    CollectBlockingSelections(ids, selected, blocking);

    StateKeyValue? head = await _store.GetKeyAsync(workspaceId, HeadNs, HeadName, ct).ConfigureAwait(false);
    string? headValue = head?.Value;
    bool targetsHead = headValue is not null && selected.Any(t => t.To == headValue);

    await RunEvidenceAsync(selected, blocking, results, ct).ConfigureAwait(false);
    bool certified = blocking.Count == 0;

    CertificationReport report;
    if (certified)
    {
      await CertifyAllAsync(workspaceId, selected, targetsHead, ct).ConfigureAwait(false);
      report = new CertificationReport(certified, !certified, results, blocking);
    }
    else
    {
      await ViolateAllAsync(workspaceId, selected, blocking, targetsHead, ct).ConfigureAwait(false);
      report = new CertificationReport(certified, !certified, results, blocking);
    }

    return report;
  }

  /// <summary>Selection-level blockers: explicitly requested ids that came back
  ///     missing, or an empty store when everything was requested implicitly.</summary>
  private static void CollectBlockingSelections(IReadOnlyList<string>? ids,
      IReadOnlyList<TransitionRecord> selected, List<string> blocking)
  {
    if (ids is { Count: > 0 })
    {
      HashSet<string> found = selected.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
      List<string> missing = [.. ids.Where(id => !found.Contains(id))];
      blocking.AddRange(missing.Select(id => $"Missing transition: {id}."));
    }
    else if (selected.Count == 0)
    {
      blocking.Add("No transitions selected (none pending).");
    }
  }

  /// <summary>Runs every attached evidence command per transition, collecting results
  ///     and a blocker for each unconfirmed one.</summary>
  private async Task RunEvidenceAsync(IReadOnlyList<TransitionRecord> selected,
      List<string> blocking, List<EvidenceResult> results, CancellationToken ct)
  {
    foreach (TransitionRecord transition in selected)
    {
      if (transition.Evidence.Count == 0)
      {
        blocking.Add($"Transition {transition.Id} has no attached evidence.");
        results.Add(new EvidenceResult("(none)", false, "no evidence attached"));
        continue;
      }

      foreach (string command in transition.Evidence)
      {
        EvidenceResult result = await _evidence.RunAsync(command, ct).ConfigureAwait(false);
        results.Add(result);
        if (!result.Confirmed)
        {
          blocking.Add($"Transition {transition.Id}: '{command}' — {result.Detail}");
        }
      }
    }
  }

  private async Task CertifyAllAsync(string workspaceId, IReadOnlyList<TransitionRecord> selected,
      bool targetsHead, CancellationToken ct)
  {
    foreach (TransitionRecord transition in selected)
    {
      await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "certified", ct).ConfigureAwait(false);
    }

    await _store.AppendEventAsync(workspaceId, "state.certified",
              JsonSerializer.Serialize(new { transitions = selected.Select(t => t.Id).ToArray() }), ct).ConfigureAwait(false);
    if (targetsHead)
    {
      _ = await _store.SetKeyCasAsync(workspaceId, CertificateNs, CertificateName,
          JsonSerializer.Serialize(new
          {
            transitions = selected.Select(t => t.Id).ToArray(),
            certifiedAt = DateTimeOffset.UtcNow,
          }), null, ct).ConfigureAwait(false);
    }
  }

  private async Task ViolateAllAsync(string workspaceId, IReadOnlyList<TransitionRecord> selected,
      List<string> blocking, bool targetsHead, CancellationToken ct)
  {
    if (targetsHead)
    {
      _ = await _store.DeleteKeyCasAsync(workspaceId, CertificateNs, CertificateName, null, ct).ConfigureAwait(false);
    }

    foreach (TransitionRecord transition in selected)
    {
      await _store.SetTransitionStatusAsync(workspaceId, transition.Id, "violated", ct).ConfigureAwait(false);
    }

    await _store.AppendEventAsync(workspaceId, "state.violated",
              JsonSerializer.Serialize(new { reasons = blocking }), ct).ConfigureAwait(false);
  }

  public async Task<CertificationReport> CheckGoalAsync(CancellationToken ct = default)
  {
    StateKeyValue? goal = await _store.GetKeyAsync(_workspace.WorkspaceId, GoalNs, GoalName, ct).ConfigureAwait(false);
    if (goal is null)
    {
      return new CertificationReport(false, true, [], ["No goal/check commands stored."]);
    }

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
    List<EvidenceResult> results = [];
    List<string> blocking = [];
    foreach (string command in commands)
    {
      EvidenceResult result = await _evidence.RunAsync(command, ct).ConfigureAwait(false);
      results.Add(result);
      if (!result.Confirmed)
      {
        blocking.Add($"'{command}' — {result.Detail}");
      }
    }
    return new CertificationReport(blocking.Count == 0, blocking.Count > 0, results, blocking);
  }

  public async Task<Result<IReadOnlyList<string>>> HistoryAsync(int limit, CancellationToken ct = default)
  {
    IReadOnlyList<StateEvent> events = await _store.GetEventsAsync(_workspace.WorkspaceId, limit, ct).ConfigureAwait(false);
    return Result.Success<IReadOnlyList<string>>(
        [.. events.Select(e => $"{e.OccurredAt:u} {e.Kind} {e.PayloadJson}")]);
  }
}
