using System.Globalization;
using System.Text.Json;

namespace eThangAgent.ToolDomain;

/// <summary>The completion gate (issue #283 Part 2): a completion claim requires a
///     FRESH passing gate, where fresh means 'run since the newest workspace change',
///     not merely 'was run once'.
///     <para>THE HARNESS KNOWS NOTHING ABOUT LINTERS. It never parses .editorconfig,
///     never learns a command's language, and ships no built-in command set. It compares
///     two facts the PROJECT supplied: the commands at <c>goal/check</c>, and the
///     outcome recorded at <c>goal/lastcheck</c>. A Go repo stores <c>go build ./...</c>;
///     a .NET repo stores <c>dotnet build</c>. The harness only asks whether the answer
///     is still current.</para>
///     <para>EVERY REFUSAL HAS A NAMED EXIT, because the cheapest way past a gate that
///     offers none is a lie. The exits here are: run the gate (<c>state.checkgoal</c>),
///     or drop the plan (<c>Abandoned</c>, always allowed). A workspace with no gate at
///     all is never blocked - the check is simply unavailable.</para></summary>
public sealed class CompletionGate(IWorkspaceSnapshotAccess snapshots)
{
  /// <summary>State key holding the project's own check commands, as a JSON array of
  ///     strings. Owned by the State Domain (<c>StateService.GoalNs/GoalName</c>).</summary>
  public const string GoalCheckKey = "goal/check";

  /// <summary>State key holding the last run's outcome. Written by the gate, read by
  ///     the evaluator - never hand-edited, and never trusted for anything but freshness.</summary>
  public const string LastCheckKey = "goal/lastcheck";

  /// <summary>State key recording that the user was asked and DECLINED a gate (issue #285 D1).
  ///     <para>A SIBLING KEY, deliberately not a sentinel inside <see cref="GoalCheckKey"/>: an
  ///     empty array there already means BROKEN CONFIG to the checkgoal runner, so storing a
  ///     decline in it would conflate a deliberate choice with a fault. Its PRESENCE is the whole
  ///     fact - the value carries when and why, for a human reading the record.</para></summary>
  public const string DeclinedKey = "goal/declined";

  /// <summary>What an unset gate says: nobody has ever been asked.
  ///     <para>WORDED TO BE TRUE RATHER THAN REASSURING: it names the state (never configured),
  ///     not merely that nothing is required, so a reader cannot mistake it for a gate that
  ///     passed.</para></summary>
  private const string UnsetMessage =
      "No completion gate is configured for this workspace (nobody has been asked), so nothing "
      + "is required of this claim.";

  /// <summary>What a DECLINED gate says (issue #285 D1). Deliberately different wording from
  ///     <see cref="UnsetMessage"/>: 'you declined one' and 'nobody asked' are different facts,
  ///     and a user auditing their own workspaces must be able to tell them apart.</summary>
  private const string DeclinedMessage =
      "A completion gate was DECLINED for this workspace, so nothing is required of this claim.";

  private readonly IWorkspaceSnapshotAccess _snapshots =
      snapshots ?? throw new ArgumentNullException(nameof(snapshots));

  /// <summary>Decides whether a completion claim may proceed, for a workspace with no gate.
  ///     <paramref name="workspaceRoot"/> is the session workspace or the child's anchor, never
  ///     the process directory.
  ///     <para>THIS OVERLOAD EXISTS SO THE #283 CALLERS STAY BYTE-IDENTICAL (issue #285). Every
  ///     pre-existing caller passes <c>(root, configured, lastCheck, ct)</c> and means exactly
  ///     this: no decline recorded. A parameter inserted before <c>ct</c> would have silently
  ///     reinterpreted a token as a bool at every call site.</para></summary>
  public Task<CompletionGateVerdict> EvaluateAsync(string workspaceRoot, bool gateConfigured,
      string? lastCheckJson, CancellationToken ct = default) =>
      EvaluateAsync(workspaceRoot, gateConfigured, lastCheckJson, gateDeclined: false, ct);

  /// <summary>Decides whether a completion claim may proceed.
  ///     <para><paramref name="gateDeclined"/> distinguishes 'the user said no' from 'nobody has
  ///     asked' (issue #285 D1). Both never block; they are different STATES, so a report can
  ///     tell them apart.</para></summary>
  public async Task<CompletionGateVerdict> EvaluateAsync(string workspaceRoot,
      bool gateConfigured, string? lastCheckJson, bool gateDeclined,
      CancellationToken ct = default)
  {
    if (!gateConfigured)
    {
      return new CompletionGateVerdict(gateDeclined ? GoalGateStatus.Declined : GoalGateStatus.Unset,
          gateDeclined ? DeclinedMessage : UnsetMessage);
    }

    if (lastCheckJson is null)
    {
      return new CompletionGateVerdict(GoalGateStatus.NeverRan, Refusal(
          "has never run in this workspace",
          "run the gate with state.checkgoal and confirm it passes"));
    }

    if (ParseRecord(lastCheckJson) is not { } record)
    {
      // A corrupt record is not evidence of anything, so it cannot license a claim.
      return new CompletionGateVerdict(GoalGateStatus.NeverRan, Refusal(
          "has no readable recorded outcome (goal/lastcheck is corrupt)",
          "re-run the gate with state.checkgoal"));
    }

    return record.Passed
        ? await FreshnessAsync(workspaceRoot, record, ct).ConfigureAwait(false)
        : new CompletionGateVerdict(GoalGateStatus.Failed, Refusal(
            string.Create(CultureInfo.InvariantCulture,
                $"last ran at {record.RanAt:u} and FAILED"),
            "fix the failure, then re-run state.checkgoal until it passes"));

  }

  /// <summary>The freshness half: a passing record is only good while the workspace
  ///     still matches the digest recorded at the run. A record with no digest cannot be
  ///     compared, which is unverifiable - never fresh by assumption.</summary>
  private async Task<CompletionGateVerdict> FreshnessAsync(string workspaceRoot,
      GoalGateRecord record, CancellationToken ct) =>
      record.Digest is null
          ? new CompletionGateVerdict(GoalGateStatus.Unverifiable,
              "[completion gate] The gate last passed, but its freshness cannot be verified "
              + "(the recorded run carries no workspace digest). This does not block the claim; "
              + "re-run state.checkgoal to make future claims verifiable.")
          : await CompareAsync(workspaceRoot, record, ct).ConfigureAwait(false);

  private async Task<CompletionGateVerdict> CompareAsync(string workspaceRoot,
      GoalGateRecord record, CancellationToken ct)
  {
    WorkspaceSnapshot current;
    try
    {
      current = await _snapshots.CaptureAsync(workspaceRoot, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    // Named decision (CA1031): an unreadable workspace must not become a refusal the
    // user cannot escape - it becomes the honest 'cannot tell' state instead.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      return Unverifiable("the workspace could not be read");
    }
#pragma warning restore CA1031 // Do not catch general exception types

    return current.IsComplete
        ? Freshness(current.Digest, record)
        : Unverifiable("the workspace walk was truncated");
  }

  /// <summary>The digest comparison, isolated so the two outcomes read as the two facts
  ///     they are rather than as one nested conditional.</summary>
  private static CompletionGateVerdict Freshness(string currentDigest, GoalGateRecord record) =>
      string.Equals(currentDigest, record.Digest, StringComparison.Ordinal)
          ? new CompletionGateVerdict(GoalGateStatus.Fresh,
              string.Create(CultureInfo.InvariantCulture,
                  $"[completion gate] The gate passed at {record.RanAt:u} and the workspace is unchanged since."))
          : new CompletionGateVerdict(GoalGateStatus.Stale, Refusal(
              string.Create(CultureInfo.InvariantCulture,
                  $"passed at {record.RanAt:u}, but the workspace has CHANGED since that run"),
              "re-run state.checkgoal so the passing result covers the current code"));

  private static CompletionGateVerdict Unverifiable(string why) =>
      new(GoalGateStatus.Unverifiable,
          $"[completion gate] Freshness cannot be verified ({why}). This does not block the "
          + "claim; re-run state.checkgoal to make future claims verifiable.");

  /// <summary>The refusal sentence, naming what happened and the exact remedy. Both
  ///     attach points render this verbatim, so the model meets the same words wherever
  ///     it hits the gate.</summary>
  private static string Refusal(string what, string remedy) =>
      $"[completion gate] This workspace's gate ({GoalCheckKey}) {what}. "
      + $"To claim completion, {remedy}. To drop the work instead, set the plan Abandoned - "
      + "that is always allowed and is the honest exit when the work is not finished.";

  /// <summary>Strict parse of the recorded outcome. Anything unreadable yields null,
  ///     which the evaluator treats as 'never ran' rather than as freshness.</summary>
  public static GoalGateRecord? ParseRecord(string json)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object
          || !root.TryGetProperty("passed", out JsonElement passed)
          || passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
          || !root.TryGetProperty("ranAt", out JsonElement ranAt)
          || !ranAt.TryGetDateTimeOffset(out DateTimeOffset ranAtValue))
      {
        return null;
      }

      string? digest = root.TryGetProperty("digest", out JsonElement digestEl)
          && digestEl.ValueKind == JsonValueKind.String
              ? digestEl.GetString()
              : null;
      return new GoalGateRecord(digest, passed.GetBoolean(), ranAtValue);
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
