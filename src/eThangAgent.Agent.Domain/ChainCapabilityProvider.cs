using System.Globalization;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The model-facing door over the chain record (issue #313 D6): one action,
///     <c>create</c>, which converts a plan into a chain and starts step 1.
///     <para>WHY HERE AND NOT ON THE PLAN PROVIDER (D6): putting it on the Plan provider
///     would make the Plan Domain reference the Agent Domain, and the chain record is
///     Agent-domain surface anyway. A plan stays a plan; converting it is an Agent-domain
///     act that READS a plan.</para>
///     <para>THE TRANSITION IS EXPLICIT, NEVER IMPLICIT (D1): a plan can exist indefinitely
///     without becoming a chain - a design artifact someone is still arguing about - so
///     something must state 'execute this'. This action is that statement.</para>
///     <para>IT REFUSES RATHER THAN GUESSING (D4). Every refusal is a typed code naming what
///     to fix: a plan with no executable steps, a position the plan does not have, two roles
///     for one step, an unreadable plan, a session that is itself a chain step (D9). A chain
///     that cannot run is never created.</para>
///     <para>THE CREATING SESSION IS NOT THE OWNER (D5). Creation claims step 1 for a
///     session that does not exist yet, starts it through
///     <see cref="IChainStartPort"/>, and reassigns the claim to the real id. The creating
///     session may close the moment it returns.</para></summary>
/// <param name="chains">The chain application service.</param>
/// <param name="plans">Reads the plan a chain would execute.</param>
/// <param name="start">Starts step 1's session.</param>
/// <param name="sessionId">The creating session's id, resolved LAZILY: the composition
///     passes the session's persisted root id, and a child's create must stamp the owning
///     ROOT session rather than the child (the Plan provider's precedent).</param>
/// <param name="clock">The clock the claim window is measured against.</param>
public sealed class ChainCapabilityProvider(ChainService chains, IChainPlanSource plans,
    IChainStartPort start, Func<string?> sessionId, TimeProvider clock) : ICapabilityProvider
{
  public const string ProviderId = "chain";

  /// <summary>The action ids this provider exposes, without constructing it - the grant
  ///     surface reads these at composition time, where resolving the provider itself would
  ///     re-enter its singleton.
  ///     <para>THE ACTION IS <c>start</c>, NOT <c>create</c>. Capability action names are
  ///     GLOBALLY unique (the registry dispatches by bare name), the Plan provider already
  ///     owns <c>create</c>, and the collision is a startup failure rather than a shadowing.
  ///     <c>start</c> is also the truer name: the action does not merely write a record, it
  ///     converts a plan into a chain AND starts step 1.</para></summary>
  public static readonly string[] ActionNames = ["start"];

  private const string PlanIdParam = "planId";
  private const string StepsParam = "steps";
  private const string PositionParam = "position";
  private const string RoleParam = "role";
  private const string ModelParam = "model";
  private const string ImplementRole = "implement";
  private const string ReviewRole = "review";

  private static readonly HashSet<string> StepAllowed =
      new(StringComparer.Ordinal) { PositionParam, RoleParam, ModelParam };

  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainPlanSource _plans = plans ?? throw new ArgumentNullException(nameof(plans));
  private readonly IChainStartPort _start = start ?? throw new ArgumentNullException(nameof(start));
  private readonly Func<string?> _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("start", "Convert a plan into a chain and start its first step.",
            """
            Converts an existing plan into a chain the HARNESS executes: it claims step 1, starts a session for it seeded with the rendered brief, and returns. No session decides when the next step begins - completing a step is what advances the chain.
            A plan does NOT become a chain on its own. This action is the explicit statement 'execute this', so a plan can stay a design artifact indefinitely.
            'steps' names which plan steps execute and in what role. Only steps you name become chain steps, so design and informational steps are simply left out. Roles are exactly implement | review; a step that names none is an implementer step. A review step may name the reviewer's model with 'model'.
            Validation is total and refuses before anything is created: a plan with no executable step (ChainNoExecutableSteps), a position the plan does not have (ChainStepMissing), one position declared with two roles (ChainStepRoleConflict), an unreadable plan (ChainPlanUnreadable). A session that is itself executing a chain step may not create a chain (ChainStepCannotCreateChain).
            Output contract:
            [chain] created #<id> from plan #<planId> v<version>: <n> step(s), step 1 started as session <sessionId>
            The creating session may close immediately; the chain is unaffected.
            """,
            [
                new ActionParameter(PlanIdParam, ActionParameterTypes.IntegerType, "Required. The plan to execute, >= 1."),
                new ActionParameter(StepsParam, "Array", "Optional. The steps to execute: [{ 'position' (required, >= 1), 'role' (optional: implement|review, default implement), 'model' (optional reviewer model) }]. Omit to execute EVERY plan step as an implementer step."),
            ],
            [PlanIdParam]),
  ];

  public async Task<CapabilityInvocationResult> InvokeAsync(string actionName, string jsonArguments,
      CancellationToken ct = default)
  {
    try
    {
      return actionName switch
      {
        "start" => await CreateAsync(jsonArguments, ct).ConfigureAwait(false),
        _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
      };
    }
    catch (ChainInputException ex)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
    }
  }

  private async Task<CapabilityInvocationResult> CreateAsync(string json, CancellationToken ct)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(PlanIdParam, StepsParam));
    int planId = ReqPlanId(args);
    string? creating = _sessionId();
    if (await NestedChainRefusalAsync(creating, ct).ConfigureAwait(false) is { } nested)
    {
      return nested;
    }

    Result<ChainPlanFacts> plan = await _plans.GetPlanAsync(planId, ct).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(new DomainError(ChainCreationCode.PlanUnreadable,
          $"plan #{planId} could not be read, so nothing was validated against it: {plan.Error.Message} "
          + "Re-read it with plan.show and try again."));
    }

    Result<IReadOnlyList<ChainStepSeed>> steps = OptSteps(args, plan.Value);
    return steps.IsSuccess
        ? await CreateAndStartAsync(plan.Value, steps.Value, creating, ct).ConfigureAwait(false)
        : Gutter(steps.Error);
  }

  /// <summary>D9's enforcement: a session executing a chain step may not start a chain.
  ///     <para>THE CHECK READS THE CHAIN RECORD, not a flag: the record already answers
  ///     'which step is this session executing', so a second source of that answer would be
  ///     a second thing that can disagree. A session in no chain passes straight through,
  ///     which is what keeps the ordinary way a plan is worked intact.</para>
  ///     <para>A chain-store fault REFUSES rather than permits: letting the creation through
  ///     on an unreadable record would nest a chain nobody can see.</para></summary>
  private async Task<CapabilityInvocationResult?> NestedChainRefusalAsync(string? sessionId,
      CancellationToken ct)
  {
    if (sessionId is null)
    {
      return null;
    }

    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    return !found.IsSuccess
        ? Gutter(new DomainError(ChainCreationCode.NestedChain,
            $"the chain record could not be read, so this session's standing is unknown: {found.Error.Message}"))
        : AlreadyExecutingRefusal(found.Value, sessionId);
  }

  /// <summary>The refusal for a session that IS executing a chain step, or null for one that
  ///     is not. Extracted so the read's two failure shapes stay separate statements rather
  ///     than a nested conditional.</summary>
  private static CapabilityInvocationResult? AlreadyExecutingRefusal(Chain? chain, string sessionId) =>
      chain is null
          ? null
          : Gutter(new DomainError(ChainCreationCode.AlreadyExecuting,
              $"this session is executing step {AssignedPosition(chain, sessionId)} of chain #{chain.Id}, "
              + "and a chain step may not start a chain. Finish this step and let the harness advance the chain."));

  /// <summary>The position this session holds in <paramref name="chain"/>, for the refusal
  ///     message. A session the record does not name cannot reach here (the store found the
  ///     chain BY that session id), so the fallback is a stated unknown rather than a guess.</summary>
  private static string AssignedPosition(Chain chain, string? sessionId) =>
      chain.Steps.FirstOrDefault(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal))
          is { } step
        ? step.Position.ToString(CultureInfo.InvariantCulture)
        : "an unnamed";

  /// <summary>Creates the record, then claims and starts step 1.
  ///     <para>ORDER IS LOAD-BEARING (the #301 advance's rule, applied at creation): the
  ///     claim happens BEFORE the launch so the step is unavailable to any other claimant
  ///     during the launch window, and it is REASSIGNED to the real id afterwards, which
  ///     spends no second attempt. A failed launch RELEASES the claim, keeping the attempt,
  ///     so the step is available again rather than parked behind a session that never
  ///     existed.</para>
  ///     <para>A START THAT FAILS LEAVES THE CHAIN CREATED. The record is a fact about the
  ///     plan; the launch is a fact about this process. Deleting the chain would destroy a
  ///     record the user can resume from, and the released step is exactly what a later
  ///     advance picks up.</para></summary>
  private async Task<CapabilityInvocationResult> CreateAndStartAsync(ChainPlanFacts plan,
      IReadOnlyList<ChainStepSeed> steps, string? sessionId, CancellationToken ct)
  {
    DateTimeOffset now = _clock.GetUtcNow();
    Result<Chain> created = await _chains.CreateAsync(
        new ChainCreation(plan.WorkspaceId, plan.PlanId, plan.Version, plan.Title, sessionId, steps),
        now, ct).ConfigureAwait(false);
    if (!created.IsSuccess)
    {
      return Gutter(created.Error);
    }

    Chain chain = created.Value;
    int first = chain.Steps[0].Position;
    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains.ClaimStepAsync(chain.Id, first, provisional, now, ct)
        .ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return Gutter(claimed.Error);
    }

    // THE ROLE TRAVELS FROM THE RECORD, not from the declaration: the record is what a
    // resumed or restarted launch reads, so a caller restating it could disagree with it.
    ChainStepAssignment assignment = new ChainStepAssignment(chain.Id, plan.PlanId, first, null)
        .WithRole(chain.Steps[0].Role);
    Result<string> started = await _start.StartFirstStepAsync(assignment, ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      _ = await _chains.ReleaseStepAsync(chain.Id, first, ChainActor.Harness, now, ct).ConfigureAwait(false);
      return Gutter(started.Error);
    }

    _ = await _chains.ReassignStepAsync(chain.Id, first, started.Value, now, ct).ConfigureAwait(false);
    return CapabilityInvocationResult.Ok(string.Create(CultureInfo.InvariantCulture,
        $"[chain] created #{chain.Id} from plan #{plan.PlanId} v{plan.Version}: "
        + $"{chain.Steps.Count} step(s), step {first} started as session {started.Value}"));
  }

  // ---- strict argument parsing --------------------------------------------

  private static HashSet<string> Allowed(params string[] names) => new(names, StringComparer.Ordinal);

  private static Dictionary<string, JsonElement> ParseArgs(string json, HashSet<string> allowed)
  {
    JsonElement root;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
      root = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      throw new ChainInputException($"Arguments are not valid JSON: {ex.Message}");
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
      throw new ChainInputException("Arguments must be a JSON object.");
    }

    Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (!allowed.Contains(property.Name))
      {
        throw new ChainInputException($"Unknown parameter '{property.Name}'.");
      }

      args[property.Name] = property.Value.Clone();
    }

    return args;
  }

  private static int ReqPlanId(Dictionary<string, JsonElement> args)
  {
    int id = !args.TryGetValue(PlanIdParam, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int parsed)
        ? throw new ChainInputException($"'{PlanIdParam}' is required and must be an integer.")
        : parsed;
    return id >= 1 ? id : throw new ChainInputException($"'{PlanIdParam}' must be >= 1.");
  }

  /// <summary>The declared steps, or EVERY plan step as an implementer step when none were
  ///     named.
  ///     <para>OMITTING 'steps' MEANS 'EXECUTE THE WHOLE PLAN', which is the common case: a
  ///     model that planned and now wants it run should not have to restate every position.
  ///     D3's 'not every step becomes a chain step' is served by NAMING the ones that do -
  ///     the omission is the shortcut, the declaration is the control.</para></summary>
  private static Result<IReadOnlyList<ChainStepSeed>> OptSteps(Dictionary<string, JsonElement> args,
      ChainPlanFacts plan)
  {
    if (!args.TryGetValue(StepsParam, out JsonElement element))
    {
      return Result.Success<IReadOnlyList<ChainStepSeed>>(
          [.. plan.StepPositions.Select(ChainStepSeed.Implementing)]);
    }

    if (element.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<ChainStepSeed>>(new DomainError("InvalidActionInput",
          $"'{StepsParam}' must be an array of step objects."));
    }

    List<ChainStepSeed> seeds = [];
    int index = 0;
    foreach (JsonElement item in element.EnumerateArray())
    {
      index++;
      if (item.ValueKind != JsonValueKind.Object)
      {
        return Result.Failure<IReadOnlyList<ChainStepSeed>>(new DomainError("InvalidActionInput",
            $"'{StepsParam}[{index}]' must be a step object."));
      }

      Result<ChainStepSeed> seed = ParseStep(item, index);
      if (!seed.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<ChainStepSeed>>(seed.Error);
      }

      seeds.Add(seed.Value);
    }

    // The plan-level rules (D4/D10) run here rather than only in the aggregate, because
    // the aggregate sees the seeds and this sees the PLAN they must fit.
    DomainError? refusal = ChainCreationPolicy.RefusalFor(seeds, plan.StepPositions);
    return refusal is null
        ? Result.Success<IReadOnlyList<ChainStepSeed>>(seeds)
        : Result.Failure<IReadOnlyList<ChainStepSeed>>(refusal);
  }

  private static Result<ChainStepSeed> ParseStep(JsonElement item, int index)
  {
    Dictionary<string, JsonElement> step = new(StringComparer.Ordinal);
    foreach (JsonProperty property in item.EnumerateObject())
    {
      if (!StepAllowed.Contains(property.Name))
      {
        return Result.Failure<ChainStepSeed>(new DomainError("InvalidActionInput",
            $"Unknown parameter '{StepsParam}[{index}].{property.Name}'."));
      }

      step[property.Name] = property.Value.Clone();
    }

    if (!step.TryGetValue(PositionParam, out JsonElement positionElement)
        || positionElement.ValueKind != JsonValueKind.Number
        || !positionElement.TryGetInt32(out int position)
        || position < 1)
    {
      return Result.Failure<ChainStepSeed>(new DomainError("InvalidActionInput",
          $"'{StepsParam}[{index}].{PositionParam}' is required and must be an integer >= 1."));
    }

    Result<ChainStepRole> role = ParseRole(step, index);
    return role.IsSuccess
        ? Result.Success(new ChainStepSeed(position, role.Value))
        : Result.Failure<ChainStepSeed>(role.Error);
  }

  /// <summary>The step's role: exactly implement | review, defaulting to implement.
  ///     <para>THE VOCABULARY IS CLOSED (D7). An unknown token is refused by name rather
  ///     than defaulted, because a role a reader cannot name is a step nobody can start
  ///     correctly - the same rule the persisted actor and status columns follow.</para>
  ///     <para>A MODEL IS ONLY MEANINGFUL ON A REVIEW STEP. 'model' on an implementer step
  ///     is refused rather than ignored: #218's separation rule reads a reviewer's model, and
  ///     silently dropping one a caller stated would leave the caller believing a constraint
  ///     was recorded.</para></summary>
  private static Result<ChainStepRole> ParseRole(Dictionary<string, JsonElement> step, int index)
  {
    Result<string?> token = OptString(step, RoleParam, index);
    if (!token.IsSuccess)
    {
      return Result.Failure<ChainStepRole>(token.Error);
    }

    Result<string?> model = OptString(step, ModelParam, index);
    return model.IsSuccess ? RoleFrom(token.Value, model.Value, index) : Result.Failure<ChainStepRole>(model.Error);
  }

  /// <summary>The role a step's two optional tokens name.
  ///     <para>A MODEL IS ONLY MEANINGFUL ON A REVIEW STEP. 'model' on an implementer step
  ///     is refused rather than ignored: #218's separation rule reads a reviewer's model, and
  ///     silently dropping one a caller stated would leave the caller believing a constraint
  ///     was recorded.</para></summary>
  private static Result<ChainStepRole> RoleFrom(string? token, string? model, int index) =>
      token switch
      {
        null when model is null => Result.Success(ChainStepRole.Implement),
        null => Result.Success(ChainStepRole.Review(model)),
        ImplementRole when model is null => Result.Success(ChainStepRole.Implement),
        ImplementRole => Result.Failure<ChainStepRole>(new DomainError("InvalidActionInput",
            $"'{StepsParam}[{index}].{ModelParam}' names a reviewer model, but the step's role is "
            + $"{ImplementRole}; only a {ReviewRole} step carries one.")),
        ReviewRole => Result.Success(ChainStepRole.Review(model)),
        _ => Result.Failure<ChainStepRole>(new DomainError("InvalidActionInput",
            $"'{StepsParam}[{index}].{RoleParam}' must be exactly {ImplementRole}|{ReviewRole}.")),
      };

  /// <summary>An optional non-empty string on a step object. A present-but-empty value is
  ///     refused rather than read as absent: the caller stated the member, so treating it as
  ///     omitted would silently discard an intention.</summary>
  private static Result<string?> OptString(Dictionary<string, JsonElement> args, string name,
      int index)
  {
    return !args.TryGetValue(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null
        ? Result.Success<string?>(null)
        : ValidateOptionalString(element, name, index);
  }

  /// <summary>A present optional string, refused when it is not a non-empty string.</summary>
  private static Result<string?> ValidateOptionalString(JsonElement element, string name, int index) =>
      element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString())
          ? Result.Failure<string?>(new DomainError("InvalidActionInput",
              $"'{StepsParam}[{index}].{name}' must be a non-empty string when present."))
          : Result.Success<string?>(element.GetString()!.Trim());

  private static CapabilityInvocationResult Gutter(DomainError error)
      => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");
}
