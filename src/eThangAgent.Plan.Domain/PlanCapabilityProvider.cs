using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>The model-facing capability door over <see cref="PlanService"/>: seven
///     actions with strict JSON argument parsing, string pre-validation before every
///     domain call, and verbatim output contracts. Parse failures render
///     'Error [InvalidActionInput]'; unknown actions render 'Error [UnknownAction]';
///     service failures render their DomainError codes verbatim. Construction takes the
///     session id as a lazy Func (composition passes
///     <c>(() => sp.GetRequiredService&lt;RootSessionIdentity&gt;().Id?.ToString())</c>), so a
///     child's create/add stamps the owning ROOT session, never the child.</summary>
public sealed class PlanCapabilityProvider(PlanService service, Func<string?> sessionId)
    : ICapabilityProvider
{
  public const string ProviderId = "plan";

  private const string IdParam = "id";
  private const string Title = "title";
  private const string Goal = "goal";
  private const string Steps = "steps";
  private const string Detail = "detail";
  private const string TodoId = "todoId";
  private const string Position = "position";
  private const string Status = "status";
  private const string PlanStepNotFoundCode = "PlanStepNotFound";
  private const string VersionConflictCode = "VersionConflict";
  private const string InvalidTransitionCode = "InvalidTransition";

  private static readonly HashSet<string> StepAllowed = new(StringComparer.Ordinal) { Title, Detail, TodoId };

  private readonly PlanService _service = service ?? throw new ArgumentNullException(nameof(service));
  private readonly Func<string?> _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("create", "Create a new structured plan with an optional initial step list.",
            "Creates an Active plan stamped with the current session id. Title and goal are required non-empty strings; steps is an optional array of step objects (each: required non-empty 'title', optional 'detail' string, optional 'todoId' integer >= 1). Output contract: '[plan] created #<id>' - and show renders the full plan with its session line. Errors: InvalidActionInput (bad params), InvalidTransition.",
            [new ActionParameter(Title, ActionParameterTypes.StringType, "Required. Non-empty plan title."),
              new ActionParameter(Goal, ActionParameterTypes.StringType, "Required. Non-empty goal; may be multi-line."),
              new ActionParameter(Steps, "Array", "Optional. Step objects: { 'title' (required non-empty), 'detail' (optional string), 'todoId' (optional integer >= 1). }")]),
        new("show", "Display one plan: envelope line, goal, and every step.",
            "Output contract:\n[plan #<id> v<version> | <status> | session <sid>] <title>\n<goal>\n<n> step(s):\n  <position>. [ <step-status> ] <title> (todo #<todoId>)? ( — <detail first line>)?\nThe goal may be multi-line; step lines are two-space indented; a plan with no steps prints '0 step(s):' and no step lines. Errors: InvalidActionInput (bad params), PlanNotFound.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1.")],
            [IdParam]),
        new("index", "List plans, optionally filtered by status.",
            "Output contract: header '[plan] <n> plan(s)' then one line per plan:\n#<id> [ <status> ] <title> (<n> step(s), session <sid>)\nAn empty list prints only the header. status, when present, must be exactly Active|Completed|Abandoned. Errors: InvalidActionInput (bad params).",
            [new ActionParameter(Status, ActionParameterTypes.StringType, "Optional. Exactly Active|Completed|Abandoned; omit for every plan.")]),
        new("add-step", "Append one step to a plan.",
            "Appends at the next 1-based position. Output contract: '[plan] added step <position> to #<id>'. Errors: InvalidActionInput (bad params), PlanNotFound, InvalidTransition (frozen plan).",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Title, ActionParameterTypes.StringType, "Required. Non-empty step title."),
              new ActionParameter(Detail, ActionParameterTypes.StringType, "Optional. Non-empty when present."),
              new ActionParameter(TodoId, ActionParameterTypes.IntegerType, "Optional. Linked todo id, >= 1.")],
            [IdParam, Title]),
        new("update-step", "Change one step's title, detail, todo link, or status.",
            "At least one optional field is required. Output contract: '[plan] updated step <position> in #<id>'. status must be exactly Pending|InProgress|Done. An unknown position fails Error [PlanStepNotFound] naming the position and plan id. VersionConflict errors instruct re-get + reconcile + retry. Errors: InvalidActionInput, PlanNotFound, PlanStepNotFound, InvalidTransition (frozen plan), VersionConflict.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Position, ActionParameterTypes.IntegerType, "Required. 1-based step position."),
              new ActionParameter(Title, ActionParameterTypes.StringType, "Optional. Non-empty when present."),
              new ActionParameter(Detail, ActionParameterTypes.StringType, "Optional. Non-empty when present."),
              new ActionParameter(TodoId, ActionParameterTypes.IntegerType, "Optional. Linked todo id, >= 1."),
              new ActionParameter(Status, ActionParameterTypes.StringType, "Optional. Exactly Pending|InProgress|Done.")],
            [IdParam, Position]),
        new("remove-step", "Remove one step; the tail renumbers.",
            "Output contract: '[plan] removed step <position> from #<id>'. An unknown position fails Error [PlanStepNotFound] naming the position and plan id. Errors: InvalidActionInput (bad params), PlanNotFound, PlanStepNotFound, InvalidTransition (frozen plan).",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Position, ActionParameterTypes.IntegerType, "Required. 1-based step position.")],
            [IdParam, Position]),
        new("set-status", "Move a plan between statuses.",
            "Only Active -> Completed / Active -> Abandoned is legal; other moves fail Error [InvalidTransition] naming the allowed moves. Output contract: '[plan] #<id> is now <status>'. Errors: InvalidActionInput (bad params), PlanNotFound, InvalidTransition.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Status, ActionParameterTypes.StringType, "Required. Exactly Active|Completed|Abandoned.")],
            [IdParam, Status]),
  ];

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    try
    {
      return actionName switch
      {
        "create" => await CreateAsync(jsonArguments).ConfigureAwait(false),
        "show" => await ShowAsync(jsonArguments).ConfigureAwait(false),
        "index" => await IndexAsync(jsonArguments).ConfigureAwait(false),
        "add-step" => await AddStepAsync(jsonArguments).ConfigureAwait(false),
        "update-step" => await UpdateStepAsync(jsonArguments).ConfigureAwait(false),
        "remove-step" => await RemoveStepAsync(jsonArguments).ConfigureAwait(false),
        "set-status" => await SetStatusAsync(jsonArguments).ConfigureAwait(false),
        _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
      };
    }
    catch (PlanInputException ex)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
    }
  }

  // ---- actions -------------------------------------------------------------

  private async Task<CapabilityInvocationResult> CreateAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(Title, Goal, Steps));
    string title = ReqString(args, Title);
    string goal = ReqString(args, Goal);
    IReadOnlyList<(string Title, string? Detail, int? TodoId)>? steps = OptSteps(args);
    Result<Plan> created = await _service.CreateAsync(
        title, goal, _sessionId() ?? string.Empty, steps, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return created.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] created #{created.Value.Id}")
        : Gutter(created.Error);
  }

  private async Task<CapabilityInvocationResult> ShowAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam));
    int id = ReqPlanId(args);
    Result<Plan> loaded = await _service.GetAsync(id).ConfigureAwait(false);
    return loaded.IsSuccess ? CapabilityInvocationResult.Ok(RenderPlan(loaded.Value)) : Gutter(loaded.Error);
  }

  private async Task<CapabilityInvocationResult> IndexAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(Status));
    Result<IReadOnlyList<Plan>> listed = await _service.ListAsync(OptStatus(args)).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return Gutter(listed.Error);
    }

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[plan] {listed.Value.Count} plan(s)");
    foreach (Plan plan in listed.Value)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"\n#{plan.Id} [ {plan.Status} ] {plan.Title} ({plan.Steps.Count} step(s), session {plan.SessionId})");
    }

    return CapabilityInvocationResult.Ok(sb.ToString());
  }

  private async Task<CapabilityInvocationResult> AddStepAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Title, Detail, TodoId));
    int id = ReqPlanId(args);
    string title = ReqString(args, Title);
    string? detail = OptString(args, Detail);
    int? todoId = OptStepTodoId(args);
    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    int expected = plan.Value.Version;
    Result<Plan> added = await _service.AddStepAsync(
        id, title, detail, todoId, expected, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return added.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] added step {added.Value.Steps.Count} to #{id}")
        : await ResolveMutationErrorAsync(id, position: null, expected, added.Error).ConfigureAwait(false);
  }

  private async Task<CapabilityInvocationResult> UpdateStepAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Position, Title, Detail, TodoId, Status));
    int id = ReqPlanId(args);
    int position = ReqStepPosition(args);
    string? title = OptString(args, Title);
    string? detail = OptString(args, Detail);
    int? todoId = OptStepTodoId(args);
    PlanStepStatus? stepStatus = OptStepStatus(args);
    if (title is null && detail is null && todoId is null && stepStatus is null)
    {
      throw new PlanInputException(
          "at least one of 'title', 'detail', 'todoId', or 'status' is required for update-step.");
    }

    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    int expected = plan.Value.Version;
    Result<Plan> updated = await _service.UpdateStepAsync(id, position, step => step with
    {
      Title = title ?? step.Title,
      Detail = detail ?? step.Detail,
      TodoId = todoId ?? step.TodoId,
      Status = stepStatus ?? step.Status,
    }, expected, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return updated.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] updated step {position} in #{id}")
        : await ResolveMutationErrorAsync(id, position, expected, updated.Error).ConfigureAwait(false);
  }

  private async Task<CapabilityInvocationResult> RemoveStepAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Position));
    int id = ReqPlanId(args);
    int position = ReqStepPosition(args);
    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    int expected = plan.Value.Version;
    Result<Plan> removed = await _service.RemoveStepAsync(
        id, position, expected, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return removed.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] removed step {position} from #{id}")
        : await ResolveMutationErrorAsync(id, position, expected, removed.Error).ConfigureAwait(false);
  }

  private async Task<CapabilityInvocationResult> SetStatusAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Status));
    int id = ReqPlanId(args);
    PlanStatus target = ReqPlanStatus(args);
    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    Result<Plan> saved = await _service.SetStatusAsync(
        id, target, plan.Value.Version, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return saved.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] #{id} is now {saved.Value.Status}")
        : Gutter(saved.Error);
  }

  // ---- rendering -----------------------------------------------------------

  private static string RenderPlan(Plan plan)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[plan #{plan.Id} v{plan.Version} | {plan.Status} | session {plan.SessionId}] {plan.Title}\n");
    _ = sb.Append(plan.Goal).Append('\n');
    _ = sb.Append(CultureInfo.InvariantCulture, $"{plan.Steps.Count} step(s):\n");
    foreach (PlanStep step in plan.Steps)
    {
      _ = sb.Append("  ").Append(CultureInfo.InvariantCulture, $"{step.Position}. [ {step.Status} ] {step.Title}");
      if (step.TodoId is { } todo)
      {
        _ = sb.Append(CultureInfo.InvariantCulture, $" (todo #{todo})");
      }

      if (!string.IsNullOrEmpty(step.Detail))
      {
        _ = sb.Append(" — ").Append(step.Detail.Split('\n')[0].TrimEnd('\r'));
      }

      _ = sb.Append('\n');
    }

    return sb.ToString();
  }

  /// <summary>Mutation-failure resolution: a version drift since the provider's load is a
  ///     VersionConflict instructing re-get + reconcile + retry; otherwise an unknown-position
  ///     aggregate rejection remaps to PlanStepNotFound naming position and plan id; every
  ///     other failure renders verbatim.</summary>
  private async Task<CapabilityInvocationResult> ResolveMutationErrorAsync(
      int planId, int? position, int expectedVersion, DomainError error)
  {
    Result<Plan> fresh = await _service.GetAsync(planId).ConfigureAwait(false);
    CapabilityInvocationResult? versionDrift = fresh.IsSuccess && fresh.Value.Version != expectedVersion
      ? Gutter(VersionConflict(planId, fresh.Value.Version))
      : null;
    return versionDrift ?? (position is { } pos && error.Code == InvalidTransitionCode
        && (!fresh.IsSuccess || !HasPosition(fresh.Value, pos))
      ? StepMissingError(planId, pos)
      : Gutter(error));
  }

  private static bool HasPosition(Plan plan, int position)
      => plan.Steps.Any(step => step.Position == position);

  private static CapabilityInvocationResult StepMissingError(int planId, int position)
      => Gutter(new DomainError(PlanStepNotFoundCode,
          $"plan #{planId} has no step at position {position}."));

  private static DomainError VersionConflict(int planId, int currentVersion)
      => new(VersionConflictCode,
          $"plan #{planId} changed while saving (now at v{currentVersion}); " +
          "re-get the plan with show, reconcile your change, and retry.");

  private static CapabilityInvocationResult Gutter(DomainError error)
      => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

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
      throw new PlanInputException($"Arguments are not valid JSON: {ex.Message}");
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
      throw new PlanInputException("Arguments must be a JSON object.");
    }

    Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (!allowed.Contains(property.Name))
      {
        throw new PlanInputException($"Unknown parameter '{property.Name}'.");
      }

      args[property.Name] = property.Value.Clone();
    }

    return args;
  }

  private static string ReqString(Dictionary<string, JsonElement> args, string name)
  {
    return !args.TryGetValue(name, out JsonElement element) || element.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(element.GetString())
      ? throw new PlanInputException($"'{name}' is required and must be a non-empty string.")
      : element.GetString()!.Trim();
  }

  private static string? OptString(Dictionary<string, JsonElement> args, string name)
  {
    return !args.TryGetValue(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null
      ? null
      : ValidateOptionalString(element, name);
  }

  private static string ValidateOptionalString(JsonElement element, string name)
  {
    return element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString())
      ? throw new PlanInputException($"'{name}' must be a non-empty string when present.")
      : element.GetString()!.Trim();
  }

  private static int? OptInt(Dictionary<string, JsonElement> args, string name)
  {
    return !args.TryGetValue(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null
      ? null
      : ValidateOptionalInt(element, name);
  }

  private static int ValidateOptionalInt(JsonElement element, string name)
  {
    return element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value)
      ? throw new PlanInputException($"'{name}' must be an integer when present.")
      : value;
  }

  private static int ReqInt(Dictionary<string, JsonElement> args, string name)
  {
    return !args.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)
        ? throw new PlanInputException($"'{name}' is required and must be an integer.")
        : value;
  }

  private static int ReqPlanId(Dictionary<string, JsonElement> args)
  {
    int id = ReqInt(args, IdParam);
    return id >= 1 ? id : throw new PlanInputException("'id' must be >= 1.");
  }

  private static int ReqStepPosition(Dictionary<string, JsonElement> args)
  {
    int position = ReqInt(args, Position);
    return position >= 1 ? position : throw new PlanInputException("'position' must be >= 1.");
  }

  private static int? OptStepTodoId(Dictionary<string, JsonElement> args)
  {
    int? todoId = OptInt(args, TodoId);
    return todoId is < 1 ? throw new PlanInputException("'todoId' must be >= 1.") : todoId;
  }

  private static List<(string Title, string? Detail, int? TodoId)>? OptSteps(
      Dictionary<string, JsonElement> args)
  {
    return !args.TryGetValue(Steps, out JsonElement element)
      ? null
      : ParseSteps(element);
  }

  private static List<(string Title, string? Detail, int? TodoId)> ParseSteps(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Array)
    {
      throw new PlanInputException("'steps' must be an array of step objects.");
    }

    List<(string Title, string? Detail, int? TodoId)> steps = [];
    int index = 0;
    foreach (JsonElement item in element.EnumerateArray())
    {
      index++;
      if (item.ValueKind != JsonValueKind.Object)
      {
        throw new PlanInputException($"'steps[{index}]' must be a step object.");
      }

      Dictionary<string, JsonElement> step = new(StringComparer.Ordinal);
      foreach (JsonProperty property in item.EnumerateObject())
      {
        if (!StepAllowed.Contains(property.Name))
        {
          throw new PlanInputException($"Unknown parameter 'steps[{index}].{property.Name}'.");
        }

        step[property.Name] = property.Value.Clone();
      }

      steps.Add((ReqString(step, Title), OptString(step, Detail), OptStepTodoId(step)));
    }

    return steps;
  }

  private static PlanStatus? OptStatus(Dictionary<string, JsonElement> args)
  {
    string? token = OptString(args, Status);
    return token is null
        ? null
        : ParsePlanStatus(token);
  }

  private static PlanStatus ReqPlanStatus(Dictionary<string, JsonElement> args)
      => ParsePlanStatus(ReqString(args, Status));

  private static PlanStatus ParsePlanStatus(string token)
  {
    return token switch
    {
      nameof(PlanStatus.Active) => PlanStatus.Active,
      nameof(PlanStatus.Completed) => PlanStatus.Completed,
      nameof(PlanStatus.Abandoned) => PlanStatus.Abandoned,
      _ => throw new PlanInputException(
            $"'status' must be exactly {nameof(PlanStatus.Active)}|{nameof(PlanStatus.Completed)}|{nameof(PlanStatus.Abandoned)}."),
    };
  }

  private static PlanStepStatus? OptStepStatus(Dictionary<string, JsonElement> args)
  {
    string? token = OptString(args, Status);
    return token is null
        ? null
        : ParseStepStatus(token);
  }

  private static PlanStepStatus ParseStepStatus(string token)
  {
    return token switch
    {
      nameof(PlanStepStatus.Pending) => PlanStepStatus.Pending,
      nameof(PlanStepStatus.InProgress) => PlanStepStatus.InProgress,
      nameof(PlanStepStatus.Done) => PlanStepStatus.Done,
      _ => throw new PlanInputException(
            $"'status' must be exactly {nameof(PlanStepStatus.Pending)}|{nameof(PlanStepStatus.InProgress)}|{nameof(PlanStepStatus.Done)}."),
    };
  }
}
