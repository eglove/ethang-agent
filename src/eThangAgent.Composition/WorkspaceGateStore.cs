using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Reads and writes a workspace's gate declaration from the DESKTOP (issue #285),
///     for a workspace that has no session open yet.
///     <para>WHY THIS CANNOT GO THROUGH A SESSION CONTAINER: the Open Workspace dialog edits the
///     workspace it is ABOUT to open, so no <c>AgentSession</c> exists to resolve <c>IStateService</c>
///     from. The durable facts live in workspace state keyed by workspace IDENTITY (issue #317),
///     and both pieces are reachable app-side: the shared <c>AppDatabase</c> and the identity
///     resolver the app already owns. So this composes them the same way a session container
///     would, over the workspace's resolved identity rather than a path.</para>
///     <para>IT REUSES THE REAL <see cref="StateService"/> RATHER THAN WRITING SQL. A second
///     writer of <c>goal/check</c> would eventually disagree with the one the gate reads - the
///     D1 rule the telemetry report follows for cost. The service's evidence runner, change bus,
///     goal runner and snapshot seam are all irrelevant to a key write, so they are passed as
///     inert collaborators and named as such.</para>
///     <para>THE DECISION IS TRI-STATE (see <see cref="GateDeclaration"/>): declare, decline, or
///     say nothing. An <c>Unchanged</c> declaration writes NOTHING - not an empty list, not a
///     cleared key - so opening a workspace without touching the gate leaves it exactly as it
///     was.</para></summary>
/// <param name="database">The app-owned database every session shares.</param>
/// <param name="identities">Resolves a directory to its stable workspace identity, so the keys
///     land where the session that opens next will look for them.</param>
public sealed class WorkspaceGateStore(AppDatabase database, IWorkspaceIdentityResolver identities)
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly IWorkspaceIdentityResolver _identities = identities
      ?? throw new ArgumentNullException(nameof(identities));

  /// <summary>The gate state of <paramref name="workspaceRoot"/>: the declared commands, or
  ///     whether one was declined.
  ///     <para>AN UNREADABLE WORKSPACE IS ITS OWN ANSWER, never an exception: the dialog must
  ///     still open, and the worst case is offering the gate for a workspace whose previous
  ///     answer could not be read.</para></summary>
  /// <param name="workspaceRoot">The directory being opened.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<WorkspaceGateState> ReadAsync(string? workspaceRoot, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return WorkspaceGateState.Unknown;
    }

    Result<string> id = await WorkspaceIdAsync(workspaceRoot, ct).ConfigureAwait(false);
    if (!id.IsSuccess)
    {
      return WorkspaceGateState.Unknown;
    }

    StateService state = StateFor(id.Value);
    Result<string> declared = await state.GetAsync(CompletionGate.GoalCheckKey, ct).ConfigureAwait(false);
    if (declared.IsSuccess)
    {
      return new WorkspaceGateState(GateStateKind.Declared, ParseCommands(declared.Value));
    }

    Result<string> declined = await state.GetAsync(CompletionGate.DeclinedKey, ct).ConfigureAwait(false);
    return declined.IsSuccess ? WorkspaceGateState.Declined : WorkspaceGateState.NeverAsked;
  }

  /// <summary>Applies <paramref name="declaration"/> to <paramref name="workspaceRoot"/>'s
  ///     workspace identity.
  ///     <para>A DECLARE CLEARS ANY EARLIER DECLINE, and a DECLINE CLEARS ANY EARLIER
  ///     DECLARATION. Both directions matter: a user who declined last month and now writes
  ///     commands must not have the stale decline suppressing the gate they just configured, and
  ///     a user who declared and now declines must not be gated by commands they no longer want.
  ///     Leaving both keys present would make the gate's answer depend on read order, which is a
  ///     bug nobody could see.</para></summary>
  /// <param name="workspaceRoot">The directory being opened.</param>
  /// <param name="declaration">What the dialog decided.</param>
  /// <param name="now">When the decision was made.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<bool>> ApplyAsync(string? workspaceRoot, GateDeclaration declaration,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(declaration);
    if (declaration.Kind == GateDeclarationKind.Unchanged)
    {
      return Result.Success(true);
    }

    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
      return Result.Failure<bool>(new DomainError("InvalidWorkspace",
          "the gate declaration needs the workspace it belongs to."));
    }

    Result<string> id = await WorkspaceIdAsync(workspaceRoot, ct).ConfigureAwait(false);
    return id.IsSuccess
        ? await WriteAsync(StateFor(id.Value), declaration, now, ct).ConfigureAwait(false)
        : Result.Failure<bool>(id.Error);
  }

  /// <summary>Writes the decided key and clears the other, so exactly one of the two facts is
  ///     ever present.</summary>
  private static async Task<Result<bool>> WriteAsync(StateService state,
      GateDeclaration declaration, DateTimeOffset now, CancellationToken ct)
  {
    if (declaration.Kind == GateDeclarationKind.Declare)
    {
      Result<StateKeyValue> saved = await state
          .SetAsync(CompletionGate.GoalCheckKey, declaration.CommandsJson(), null, ct)
          .ConfigureAwait(false);
      if (!saved.IsSuccess)
      {
        return Result.Failure<bool>(saved.Error);
      }

      _ = await state.DeleteAsync(CompletionGate.DeclinedKey, null, ct).ConfigureAwait(false);
      return Result.Success(true);
    }

    Result<StateKeyValue> declined = await state
        .SetAsync(CompletionGate.DeclinedKey, declaration.DeclineJson(now), null, ct)
        .ConfigureAwait(false);
    if (!declined.IsSuccess)
    {
      return Result.Failure<bool>(declined.Error);
    }

    _ = await state.DeleteAsync(CompletionGate.GoalCheckKey, null, ct).ConfigureAwait(false);
    return Result.Success(true);
  }

  /// <summary>The stored commands, or an empty list when the value is not a JSON array of
  ///     strings. Tolerant here on purpose: this only PREFILLS an editor, and a corrupt value
  ///     must not stop the user from replacing it.</summary>
  private static IReadOnlyList<string> ParseCommands(string stored)
  {
    try
    {
      return JsonSerializer.Deserialize<List<string>>(stored) is { } commands
          ? [.. commands.Where(command => !string.IsNullOrWhiteSpace(command))]
          : [];
    }
    catch (JsonException)
    {
      return [];
    }
  }

  /// <summary>The workspace's stable identity for <paramref name="workspaceRoot"/>.
  ///     <para>RESOLVED, NOT ASSUMED: issue #317 made the id independent of the path, so writing
  ///     under the path would put the gate where the session that opens next would never look.
  ///     </para></summary>
  private async Task<Result<string>> WorkspaceIdAsync(string workspaceRoot, CancellationToken ct)
  {
    Result<WorkspaceIdentityResolution> resolved = await _identities
        .ResolveAsync(workspaceRoot, ct).ConfigureAwait(false);
    return resolved.IsSuccess
        ? Result.Success(resolved.Value.Id)
        : Result.Failure<string>(resolved.Error);
  }

  /// <summary>A <see cref="StateService"/> over one workspace id, with inert collaborators:
  ///     a key write touches none of them, and wiring the real ones here would drag the app's
  ///     evidence runner and snapshot walk into a dialog.</summary>
  private StateService StateFor(string workspaceId) => new(
      new SqliteStateStore(_database),
      new InertEvidence(),
      FixedWorkspaceContext.ForIdentity(workspaceId),
      new InertChangeBus(),
      new InertGoalRunner(),
      null);

  /// <summary>An evidence runner this surface never reaches.</summary>
  private sealed class InertEvidence : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  /// <summary>A goal runner this surface never reaches: a declaration is not a run.</summary>
  private sealed class InertGoalRunner : IGoalCommandRunner
  {
    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  /// <summary>An empty change bus: nothing subscribes while a dialog writes a key.</summary>
  private sealed class InertChangeBus : IStateChangeBus
  {
    public IDisposable Subscribe(Action<StateChange> observer)
    {
      ArgumentNullException.ThrowIfNull(observer);
      return new Lease();
    }

    public void Publish(StateChange change)
    {
      // No subscribers on this path.
    }

    private sealed class Lease : IDisposable
    {
      public void Dispose()
      {
        // Nothing was subscribed.
      }
    }
  }
}

/// <summary>Which of the three gate states a workspace is in (issue #285).</summary>
public enum GateStateKind
{
  /// <summary>Nobody has ever asked, or the workspace could not be read.</summary>
  NeverAsked,

  /// <summary>Commands are declared.</summary>
  Declared,

  /// <summary>The user was asked and declined.</summary>
  Declined,
}

/// <summary>A workspace's gate state as the dialog reads it (issue #285).
///     <para><see cref="Commands"/> is empty for every kind but <see cref="GateStateKind.Declared"/>.
///     </para></summary>
/// <param name="Kind">Which state the workspace is in.</param>
/// <param name="Commands">The declared commands, in stored order.</param>
public sealed record WorkspaceGateState(GateStateKind Kind, IReadOnlyList<string> Commands)
{
  /// <summary>The workspace could not be read, so its state is unknown. Treated as never asked
  ///     rather than as declined: offering the question again is recoverable, silently assuming a
  ///     decision the user never made is not.</summary>
  public static WorkspaceGateState Unknown { get; } = new(GateStateKind.NeverAsked, []);

  /// <summary>Never asked: the dialog should offer the question.</summary>
  public static WorkspaceGateState NeverAsked { get; } = new(GateStateKind.NeverAsked, []);

  /// <summary>The user declined: the dialog must NOT ask again.</summary>
  public static WorkspaceGateState Declined { get; } = new(GateStateKind.Declined, []);

  /// <summary>True when the dialog should offer the question: nobody has answered yet.</summary>
  public bool NeedsAnswer => Kind == GateStateKind.NeverAsked;
}
