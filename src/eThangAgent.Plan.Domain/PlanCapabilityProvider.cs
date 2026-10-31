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
public sealed class PlanCapabilityProvider(PlanService service, Func<string?> sessionId,
    IPlanCompletionGate? completionGate = null, IPlanStepOwnershipGate? ownershipGate = null,
    Func<PlanReviewDraft, CancellationToken, Task<Result<PlanReview>>>? recordReview = null)
    : ICapabilityProvider
{
  public const string ProviderId = "plan";

  private const string IdParam = "id";
  private const string Title = "title";
  private const string Goal = "goal";
  private const string Steps = "steps";
  private const string Detail = "detail";
  private const string Position = "position";
  private const string Status = "status";
  private const string PlanStepNotFoundCode = "PlanStepNotFound";
  private const string VersionConflictCode = "VersionConflict";
  private const string InvalidTransitionCode = "InvalidTransition";
  private const string VerdictParam = "verdict";
  private const string ScoresParam = "scores";
  private const string FindingsParam = "findings";
  private const string NotesParam = "notes";
  private const string CoverageField = "requirementCoverage";
  private const string TestabilityField = "testability";
  private const string BlastRadiusField = "blastRadius";
  private const string FindingText = "text";
  private const string FindingCitation = "citation";

  /// <summary>The typed code a completion refused for want of a fresh gate carries
  ///     (issue #283). Distinct from InvalidTransition so a caller can tell 'your steps
  ///     are unfinished' from 'your gate is not current'.</summary>
  public const string CompletionGateCode = "CompletionGateNotFresh";

  /// <summary>The typed code a completion refused because the session was not assigned the
  ///     step carries (issue #301 D3). Distinct from InvalidTransition so a caller can tell
  ///     'your steps are unfinished' from 'this is not your step'.</summary>
  public const string StepNotAssignedCode = "ChainStepNotAssigned";

  private static readonly HashSet<string> StepAllowed = new(StringComparer.Ordinal) { Title, Detail };

  private readonly PlanService _service = service ?? throw new ArgumentNullException(nameof(service));
  private readonly Func<string?> _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
  private readonly IPlanCompletionGate? _completionGate = completionGate;
  private readonly IPlanStepOwnershipGate? _ownershipGate = ownershipGate;
  private readonly Func<PlanReviewDraft, CancellationToken, Task<Result<PlanReview>>>? _recordReview = recordReview;

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("create", "Create a new structured plan with an optional initial step list.",
            "Creates an Active plan stamped with the current session id. Title and goal are required non-empty strings; steps is an optional array of step objects (each: required non-empty 'title', optional 'detail' string). Output contract: '[plan] created #<id>' - and show renders the full plan with its session line. Errors: InvalidActionInput (bad params).",
            [new ActionParameter(Title, ActionParameterTypes.StringType, "Required. Non-empty plan title."),
              new ActionParameter(Goal, ActionParameterTypes.StringType, "Required. Non-empty goal; may be multi-line."),
              new ActionParameter(Steps, "Array", "Optional. Step objects: { 'title' (required non-empty), 'detail' (optional string). }")]),
        new("show", "Display one plan: envelope line, goal, and every step.",
            "Output contract:\n[plan #<id> v<version> | <status> | session <sid>] <title>\n<goal>\n<n> step(s):\n  <position>. [ <step-status> ] <title> ( — <detail first line>)?\nThe goal may be multi-line; step lines are two-space indented; a plan with no steps prints '0 step(s):' and no step lines. Errors: InvalidActionInput (bad params), PlanNotFound.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1.")],
            [IdParam]),
        new("index", "List plans, optionally filtered by status.",
            "Output contract: header '[plan] <n> plan(s)' then one line per plan:\n#<id> [ <status> ] <title> (<n> step(s), session <sid>)\nAn empty list prints only the header. A plan row that cannot be read is NOT fatal: it is skipped and reported on its own line '#<id> [ CorruptPlanRow ] <reason>' after the healthy plans, so one bad row never blanks the catalog (issue #279). status, when present, must be exactly Active|Completed|Abandoned. Errors: InvalidActionInput (bad params).",
            [new ActionParameter(Status, ActionParameterTypes.StringType, "Optional. Exactly Active|Completed|Abandoned; omit for every plan.")]),
        new("add-step", "Append one step to a plan.",
            "Appends at the next 1-based position. Output contract: '[plan] added step <position> to #<id>'. Errors: InvalidActionInput (bad params), PlanNotFound, InvalidTransition (frozen plan), VersionConflict.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Title, ActionParameterTypes.StringType, "Required. Non-empty step title."),
              new ActionParameter(Detail, ActionParameterTypes.StringType, "Optional. Non-empty when present.")],
            [IdParam, Title]),
        new("update-step", "Change one step's title, detail, or status.",
            "At least one optional field is required. Output contract: '[plan] updated step <position> in #<id>'. status must be exactly Pending|InProgress|Done|Skipped. An unknown position fails Error [PlanStepNotFound] naming the position and plan id. A session executing a CHAIN step may mark only the step it was started for Done; completing any other step fails Error [ChainStepNotAssigned] naming the step it IS assigned. A session outside a chain is unaffected. COMPLETING A STEP IS A COMPLETION CLAIM, so when the workspace declares a gate (goal/check) a Done also requires that gate to be FRESH: a stale, failed, or never-run gate fails Error [CompletionGateNotFresh] naming the remedy, and an unset or declined gate never blocks (the same rule plan completion follows - one mechanism, one command set, D1). VersionConflict errors instruct re-get + reconcile + retry. Errors: InvalidActionInput, PlanNotFound, PlanStepNotFound, ChainStepNotAssigned, CompletionGateNotFresh, InvalidTransition (frozen plan), VersionConflict.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Position, ActionParameterTypes.IntegerType, "Required. 1-based step position."),
              new ActionParameter(Title, ActionParameterTypes.StringType, "Optional. Non-empty when present."),
              new ActionParameter(Detail, ActionParameterTypes.StringType, "Optional. Non-empty when present."),
              new ActionParameter(Status, ActionParameterTypes.StringType, "Optional. Exactly Pending|InProgress|Done|Skipped.")],
            [IdParam, Position]),
        new("remove-step", "Remove one step; the tail renumbers.",
            "Output contract: '[plan] removed step <position> from #<id>'. An unknown position fails Error [PlanStepNotFound] naming the position and plan id. Errors: InvalidActionInput (bad params), PlanNotFound, PlanStepNotFound, InvalidTransition (frozen plan), VersionConflict.",
            [new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
              new ActionParameter(Position, ActionParameterTypes.IntegerType, "Required. 1-based step position.")],
            [IdParam, Position]),
        new("review", "Record a review verdict with its rubric scores and cited findings.",
              "Records a review of THIS plan version: the verdict, the three rubric dimensions, and every finding with its citation. A reviewer must call this BEFORE marking its review step done - the harness reads the RECORD, and a review step that completes with no review recorded is refused. Output contract: '[plan] recorded <verdict> review #<id> of #<planId> v<version>' followed by one line per finding, each carrying its citation. verdict must be exactly approve|deny|approve-with-conditions; scores are non-negative integers for requirement-coverage, testability, and blast-radius; every finding needs both text and a citation (a finding that cites nothing is refused, and the refusal names its index). Errors: InvalidActionInput (bad params), UnknownVerdict, UncitedFinding, InvalidRubricScore, PlanNotFound.",
              [
                new ActionParameter(IdParam, ActionParameterTypes.IntegerType, "Required. Plan id, >= 1."),
                new ActionParameter(VerdictParam, ActionParameterTypes.StringType, "Required. Exactly approve|deny|approve-with-conditions."),
                new ActionParameter(ScoresParam, "Object", "Required. { 'requirementCoverage', 'testability', 'blastRadius' } - non-negative integers."),
                new ActionParameter(FindingsParam, "Array", "Required. Findings: [{ 'text' (non-empty), 'citation' (non-empty: a requirement id or a step position) }]. An empty array is legal for a clean approve and refused for a denial."),
                new ActionParameter(NotesParam, ActionParameterTypes.StringType, "Optional. Free-text context from the reviewer."),
              ],
              [IdParam, VerdictParam, ScoresParam, FindingsParam]),
        new("set-status", "Move a plan between statuses.",
            "Only Active -> Completed / Active -> Abandoned is legal; other moves fail Error [InvalidTransition] naming the allowed moves. COMPLETION REQUIRES A CONSISTENT PLAN: while any step is Pending or InProgress the move is refused with Error [InvalidTransition] naming the offending step positions and statuses, and the plan's stored status is unchanged - mark each step Done, or Skipped when you are deliberately not doing it, with update-step first. Abandoned is unaffected: dropping a half-finished plan stays possible. Output contract: '[plan] #<id> is now <status>'. Errors: InvalidActionInput (bad params), PlanNotFound, InvalidTransition, VersionConflict.",
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
        "review" => await ReviewAsync(jsonArguments).ConfigureAwait(false),
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
    IReadOnlyList<(string Title, string? Detail)>? steps = OptSteps(args);
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
    Result<PlanCatalog> listed = await _service.ListAsync(OptStatus(args)).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return Gutter(listed.Error);
    }

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[plan] {listed.Value.Plans.Count} plan(s)");
    foreach (Plan plan in listed.Value.Plans)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"\n#{plan.Id} [ {plan.Status} ] {plan.Title} ({plan.Steps.Count} step(s), session {plan.SessionId})");
    }

    // One unreadable row is reported, never fatal (issue #279): the listing above is
    // complete for every healthy plan, and each corrupt one gets its own named line.
    foreach (CorruptPlanRow row in listed.Value.Corrupt)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n#{row.PlanId} [ CorruptPlanRow ] {row.Reason}");
    }

    return CapabilityInvocationResult.Ok(sb.ToString());
  }

  private async Task<CapabilityInvocationResult> AddStepAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Title, Detail));
    int id = ReqPlanId(args);
    string title = ReqString(args, Title);
    string? detail = OptString(args, Detail);
    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    int expected = plan.Value.Version;
    Result<Plan> added = await _service.AddStepAsync(
        id, title, detail, expected, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return added.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] added step {added.Value.Steps.Count} to #{id}")
        : await ResolveMutationErrorAsync(id, position: null, expected, added.Error).ConfigureAwait(false);
  }

  private async Task<CapabilityInvocationResult> UpdateStepAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(IdParam, Position, Title, Detail, Status));
    int id = ReqPlanId(args);
    int position = ReqStepPosition(args);
    string? title = OptString(args, Title);
    string? detail = OptString(args, Detail);
    PlanStepStatus? stepStatus = OptStepStatus(args);
    RequireAtLeastOneField(title, detail, stepStatus);
    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    // Issue #301 D3: a session executing a chain step may complete only THAT step. A session
    // started by hand is not in a chain and passes straight through, so the ordinary way a
    // plan has always been worked is untouched. Checked BEFORE the write, so a refusal leaves
    // the plan byte-identical and the version unspent.
    if (stepStatus == PlanStepStatus.Done
        && await OwnershipRefusalAsync(id, position).ConfigureAwait(false) is { } refusal)
    {
      return refusal;
    }

    // ISSUE #304 D1: THE SAME GATE, ONE LEVEL DOWN. A step claiming Done is a completion
    // claim, and 'done' means the same thing at step level as at plan level - so it is the
    // same check against the same workspace-declared command set, never a second evaluator.
    // Checked BEFORE the write, so a refusal leaves the plan byte-identical and the version
    // unspent; ownership runs first because 'this is not your step' is the more specific
    // answer. Skipped is deliberately NOT gated: it is the truthful exit the completion
    // invariant depends on, and gating it would make the honest answer unreachable.
    if (stepStatus == PlanStepStatus.Done
        && await StepCompletionGateRefusalAsync().ConfigureAwait(false) is { } gateRefusal)
    {
      return gateRefusal;
    }

    int expected = plan.Value.Version;
    Result<Plan> updated = await _service.UpdateStepAsync(id, position, step => step with
    {
      Title = title ?? step.Title,
      Detail = detail ?? step.Detail,
      Status = stepStatus ?? step.Status,
    }, expected, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return updated.IsSuccess
        ? CapabilityInvocationResult.Ok($"[plan] updated step {position} in #{id}")
        : await ResolveMutationErrorAsync(id, position, expected, updated.Error).ConfigureAwait(false);
  }

  private static void RequireAtLeastOneField(string? title, string? detail, PlanStepStatus? stepStatus)
  {
    if (title is null && detail is null && stepStatus is null)
    {
      throw new PlanInputException(
          "at least one of 'title', 'detail', or 'status' is required for update-step.");
    }
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

    // The completion gate (issue #283) runs BEFORE the transition, so a refusal leaves
    // the stored status untouched - the plan stays Active and the work stays open.
    // Abandoned is deliberately never gated: it is the named exit every refusal points
    // at, and gating it would trap a user in a plan they want to stop.
    if (target == PlanStatus.Completed
        && await CompletionGateRefusalAsync().ConfigureAwait(false) is { } refusal)
    {
      return refusal;
    }

    Result<Plan> saved = await _service.SetStatusAsync(
        id, target, plan.Value.Version, DateTimeOffset.UtcNow).ConfigureAwait(false);
    return saved.IsSuccess
        ? CapabilityInvocationResult.Ok(
            string.Create(CultureInfo.InvariantCulture, $"[plan] #{id} is now {saved.Value.Status}"))
        : await ResolveMutationErrorAsync(id, position: null, plan.Value.Version, saved.Error).ConfigureAwait(false);
  }

  /// <summary>Records a reviewer's verdict into the plan review record (issue #320) - the
  ///     writer nothing in this harness had before, and the reason a denial could not exist.
  ///     <para>THE VERSION AND THE SESSION COME FROM THE HARNESS, not the caller: the record is
  ///     keyed on the plan version JUDGED (#216 D1), so a reviewer that named its own version
  ///     could record a verdict against content nobody reviewed, and the reviewer session id is
  ///     what the chain cursor keys on. The provider reads the plan to learn the current version.
  ///     </para>
  ///     <para>A PROVIDER WITH NO RECORDER REFUSES rather than silently succeeding: an action
  ///     that reported 'recorded' while writing nothing would be the silent discard this issue
  ///     closes, wearing a new coat.</para></summary>
  private async Task<CapabilityInvocationResult> ReviewAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json,
        Allowed(IdParam, VerdictParam, ScoresParam, FindingsParam, NotesParam));
    int id = ReqPlanId(args);
    PlanReviewVerdict verdict = ReqVerdict(args);
    PlanReviewScores scores = ReqScores(args);
    IReadOnlyList<PlanReviewFinding> findings = ReqFindings(args);
    string? notes = OptString(args, NotesParam);
    RequireDenialFindings(verdict, findings);
    if (_recordReview is null)
    {
      return Gutter(new DomainError("ReviewUnsupported",
          "this host cannot record a plan review; the verdict was NOT saved."));
    }

    Result<Plan> plan = await _service.GetAsync(id).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Gutter(plan.Error);
    }

    Result<PlanReview> recorded = await _recordReview(
        new PlanReviewDraft(id, verdict, scores, findings, notes), CancellationToken.None)
        .ConfigureAwait(false);
    return recorded.IsSuccess
        ? CapabilityInvocationResult.Ok(RenderReview(recorded.Value, plan.Value.Version))
        : Gutter(recorded.Error);
  }

  /// <summary>A DENIAL WITH NO FINDINGS IS REFUSED (issue #219's surviving rule): a session
  ///     told 'the reviewer denied' with no notes will guess, and guessing is what the rule
  ///     exists to prevent. An approve with no findings is a clean pass and is legal.</summary>
  /// <param name="verdict">The verdict the reviewer stated.</param>
  /// <param name="findings">The findings it supplied.</param>
  private static void RequireDenialFindings(PlanReviewVerdict verdict,
      IReadOnlyList<PlanReviewFinding> findings)
  {
    if (verdict == PlanReviewVerdict.Deny && findings.Count == 0)
    {
      throw new PlanInputException(
          "a deny must carry at least one cited finding: a fix session told only that the reviewer "
          + "denied would have nothing to act on.");
    }
  }

  /// <summary>The recorded review, stated so the reviewer can see what was kept: the verdict,
  ///     the row id, the version judged, and every finding with its citation.</summary>
  /// <param name="review">The persisted review.</param>
  /// <param name="version">The plan version it judged.</param>
  private static string RenderReview(PlanReview review, int version)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[plan] recorded {PlanReviewVerdictCodec.Encode(review.Verdict)} review #{review.Id} of "
        + $"#{review.PlanId} v{version}");
    foreach (PlanReviewFinding finding in review.Findings)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"\n  - {finding.Text} [cites: {finding.Citation}]");
    }

    return sb.ToString();
  }

  /// <summary>The verdict token, decoded STRICTLY by the record's own codec so the accepted
  ///     spellings have one home (the brief publishes them, and #306's test pins them).
  ///     </summary>
  /// <param name="args">The parsed arguments.</param>
  private static PlanReviewVerdict ReqVerdict(Dictionary<string, JsonElement> args)
  {
    Result<PlanReviewVerdict> decoded = PlanReviewVerdictCodec.Decode(OptString(args, VerdictParam));
    return decoded.IsSuccess ? decoded.Value : throw new PlanInputException(decoded.Error.Message);
  }

  /// <summary>The three rubric dimensions, each a non-negative integer. The scale is the
  ///     rubric's (#217); this reads the numbers and enforces only what the record enforces.
  ///     </summary>
  /// <param name="args">The parsed arguments.</param>
  private static PlanReviewScores ReqScores(Dictionary<string, JsonElement> args)
  {
    if (!args.TryGetValue(ScoresParam, out JsonElement element)
        || element.ValueKind != JsonValueKind.Object)
    {
      throw new PlanInputException(
          $"'{ScoresParam}' is required and must be an object with '{CoverageField}', "
          + $"'{TestabilityField}', and '{BlastRadiusField}'.");
    }

    Result<PlanReviewScores> scores = PlanReviewScores.Create(
        ReqIntField(element, CoverageField), ReqIntField(element, TestabilityField),
        ReqIntField(element, BlastRadiusField));
    return scores.IsSuccess ? scores.Value : throw new PlanInputException(scores.Error.Message);
  }

  /// <summary>One integer field of the scores object.</summary>
  /// <param name="element">The scores object.</param>
  /// <param name="name">The field name.</param>
  private static int ReqIntField(JsonElement element, string name)
  {
    JsonProperty? found = element.EnumerateObject()
        .Cast<JsonProperty?>()
        .FirstOrDefault(property =>
            string.Equals(property!.Value.Name, name, StringComparison.Ordinal));
    return found is not { } property
        ? throw new PlanInputException($"'{ScoresParam}.{name}' is required.")
        : ReadInt(property, name);
  }

  /// <summary>One scores field's integer value, refused when it is not an integer.</summary>
  /// <param name="property">The field.</param>
  /// <param name="name">Its name, for the refusal.</param>
  private static int ReadInt(JsonProperty property, string name)
  {
    return property.Value.ValueKind == JsonValueKind.Number
        && property.Value.TryGetInt32(out int value)
        ? value
        : throw new PlanInputException($"'{ScoresParam}.{name}' must be an integer.");
  }

  /// <summary>The cited findings. EVERY finding must say something and cite something, and the
  ///     refusal names the offender's index so a reviewer with twelve findings is not told only
  ///     that 'something' was uncited.</summary>
  /// <param name="args">The parsed arguments.</param>
  private static List<PlanReviewFinding> ReqFindings(Dictionary<string, JsonElement> args)
  {
    if (!args.TryGetValue(FindingsParam, out JsonElement element)
        || element.ValueKind != JsonValueKind.Array)
    {
      throw new PlanInputException($"'{FindingsParam}' is required and must be an array.");
    }

    List<PlanReviewFinding> findings = [];
    int index = 0;
    foreach (JsonElement item in element.EnumerateArray())
    {
      index++;
      Result<PlanReviewFinding> finding = ParseFinding(item, index);
      if (!finding.IsSuccess)
      {
        throw new PlanInputException(finding.Error.Message);
      }

      findings.Add(finding.Value);
    }

    return findings;
  }

  /// <summary>One finding, refused by name when it is not an object or cites nothing.</summary>
  /// <param name="item">The finding element.</param>
  /// <param name="index">Its 1-based position, for the refusal.</param>
  private static Result<PlanReviewFinding> ParseFinding(JsonElement item, int index)
  {
    if (item.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<PlanReviewFinding>(new DomainError("InvalidActionInput",
          $"'{FindingsParam}[{index}]' must be a finding object."));
    }

    string? text = null;
    string? citation = null;
    foreach (JsonProperty property in item.EnumerateObject())
    {
      if (string.Equals(property.Name, FindingText, StringComparison.Ordinal))
      {
        text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
      }
      else if (string.Equals(property.Name, FindingCitation, StringComparison.Ordinal))
      {
        citation = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()
            : null;
      }
      else
      {
        return Result.Failure<PlanReviewFinding>(new DomainError("InvalidActionInput",
            $"Unknown parameter '{FindingsParam}[{index}].{property.Name}'."));
      }
    }

    return PlanReviewFinding.Create(text, citation);
  }

  /// <summary>Asks the ownership gate; null means 'proceed'. A provider with no gate wired
  ///     (a headless host, or a test that composes the provider directly) behaves exactly as
  ///     before - which is also the correct behaviour for a session outside any chain.</summary>
  private async Task<CapabilityInvocationResult?> OwnershipRefusalAsync(int planId, int position)
  {
    if (_ownershipGate is null || _sessionId() is not { } session)
    {
      return null;
    }

    PlanStepOwnership ownership = await _ownershipGate
        .EvaluateAsync(planId, position, session).ConfigureAwait(false);
    return ownership.Allowed
        ? null
        : CapabilityInvocationResult.Fail($"Error [{ownership.Code}]: {ownership.Message}");
  }

  /// <summary>Asks the completion gate; null means 'proceed'. A plan provider with no
  ///     gate wired behaves exactly as before, so every existing caller and test keeps
  ///     its contract.</summary>
  private async Task<CapabilityInvocationResult?> CompletionGateRefusalAsync()
  {
    if (_completionGate is null)
    {
      return null;
    }

    (bool allowed, string message) = await _completionGate.EvaluateAsync().ConfigureAwait(false);
    return allowed ? null : Gutter(new DomainError(CompletionGateCode, message));
  }

  /// <summary>The STEP-level gate refusal (issue #304 D1): the same gate, with the step's own
  ///     exit appended.
  ///     <para>WHY THE SUFFIX AND NOT A SECOND MESSAGE: the gate's sentence is the one every
  ///     refusal renders verbatim, so the gate itself stays the single source of the wording.
  ///     What differs at step level is the truthful exit - a step is deliberately skipped far
  ///     more often than a whole plan is dropped - and the gate's own message names only the
  ///     plan's exit because a plan is all it knew about. Adding the missing exit here keeps
  ///     D3 ('every refusal names its exit') true without a second copy of the reasoning.
  ///     </para></summary>
  private async Task<CapabilityInvocationResult?> StepCompletionGateRefusalAsync()
  {
    if (_completionGate is null)
    {
      return null;
    }

    (bool allowed, string message) = await _completionGate.EvaluateAsync().ConfigureAwait(false);
    return allowed
        ? null
        : Gutter(new DomainError(CompletionGateCode,
            message + " To record that this step is deliberately NOT being done instead, set it "
            + "Skipped - that is the truthful exit and it is always allowed."));
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

  private static List<(string Title, string? Detail)>? OptSteps(
      Dictionary<string, JsonElement> args)
  {
    return !args.TryGetValue(Steps, out JsonElement element)
      ? null
      : ParseSteps(element);
  }

  private static List<(string Title, string? Detail)> ParseSteps(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Array)
    {
      throw new PlanInputException("'steps' must be an array of step objects.");
    }

    List<(string Title, string? Detail)> steps = [];
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

      steps.Add((ReqString(step, Title), OptString(step, Detail)));
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
      nameof(PlanStepStatus.Skipped) => PlanStepStatus.Skipped,
      _ => throw new PlanInputException(
            $"'status' must be exactly {nameof(PlanStepStatus.Pending)}|{nameof(PlanStepStatus.InProgress)}|{nameof(PlanStepStatus.Done)}|{nameof(PlanStepStatus.Skipped)}."),
    };
  }
}
