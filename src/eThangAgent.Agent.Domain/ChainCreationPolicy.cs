using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The named refusals a chain creation can earn (issue #313 D4). Each is a distinct
///     answer a caller acts on differently, so collapsing them into one code would tell a
///     session to fix its roles when the real problem is its plan.</summary>
public static class ChainCreationCode
{
  /// <summary>The plan names no step this chain could execute (D10). An empty chain that
  ///     immediately reports Completed is a lie about work that did not happen.</summary>
  public const string NoExecutableSteps = "ChainNoExecutableSteps";

  /// <summary>A declared position does not exist in the plan. Chains bind to plan steps BY
  ///     POSITION, so a position the plan does not have names work nobody can do.</summary>
  public const string StepMissing = "ChainStepMissing";

  /// <summary>One position was declared twice with different roles.</summary>
  public const string ConflictingRole = "ChainStepRoleConflict";

  /// <summary>The plan itself could not be read, so nothing can be validated against it.
  ///     Distinct from a plan that was read and found wanting: the caller's input may be
  ///     perfectly correct, and re-reading is the remedy.</summary>
  public const string PlanUnreadable = "ChainPlanUnreadable";

  /// <summary>A chain step may not start a chain (D9). Nested chains are a later question,
  ///     and v1 refuses them so the depth question stays closed.</summary>
  public const string NestedChain = "ChainNestedNotAllowed";

  /// <summary>The session asking is already executing a chain step. One session runs one
  ///     chain step; a step that created a chain would own two cursors at once, and the
  ///     ownership gate resolves 'which step is this session executing' to exactly one.
  ///     <para>This is D9's enforcement point, not a second rule: D9 says a chain step may
  ///     not start a chain, and the way to know a session IS a chain step's session is the
  ///     chain record's own claim.</para></summary>
  public const string AlreadyExecuting = "ChainStepCannotCreateChain";
}

/// <summary>What a plan must be for a chain to execute it (issue #313 D4). PURE: facts in,
///     refusal out, no I/O and no clock - the caller reads the plan and this decides.
///     <para>WHY PURE AND SEPARATE FROM THE AGGREGATE: the aggregate can only check what it
///     is handed. Whether a position EXISTS is a fact about the PLAN, which the Agent Domain
///     does not reference; the caller supplies the plan's step positions as facts, and this
///     rule judges them. That keeps the Agent Domain ignorant of what a plan is while still
///     making the validation a single named rule rather than a caller's private checks.</para>
///     <para>EACH CHECK IS ITS OWN HELPER RETURNING A REFUSAL OR NULL, folded with <c>??</c>
///     - the <see cref="ChainAdvancePolicy"/> shape, so every refusal stays a separate named
///     rule with its own code instead of one guard chain whose branches drift apart.</para></summary>
public static class ChainCreationPolicy
{
  /// <summary>The first refusal the declaration earns, or null when it may be created.
  ///     <para>ORDER IS THE ORDER THEY MATTER IN: is there anything to execute at all,
  ///     then does every named position exist, then does any position carry two roles.</para></summary>
  /// <param name="steps">The declared steps.</param>
  /// <param name="planStepPositions">Every position the plan actually has.</param>
  public static DomainError? RefusalFor(IReadOnlyList<ChainStepSeed> steps,
      IReadOnlyList<int> planStepPositions)
  {
    ArgumentNullException.ThrowIfNull(steps);
    ArgumentNullException.ThrowIfNull(planStepPositions);
    return NoExecutableSteps(steps)
        ?? StepMissing(steps, planStepPositions)
        ?? ConflictingRole(steps)
        ?? ReviewerWithoutModel(steps);
  }

  /// <summary>D1/B1 (issue #218): a review step must NAME its reviewer model, refused at the
  ///     declaration.
  ///     <para>WHY THIS CHECK LIVES AT CREATION AS WELL AS AT SESSION START. The separation
  ///     COMPARISON cannot run here - it needs the implementer's recorded model, which does not
  ///     exist until the implementer has run - but the MISSING-MODEL half needs nothing but the
  ///     declaration, and a chain that cannot ever start its review step should be refused
  ///     before it is created rather than parked after its implementer has already spent a
  ///     session. Both call the SAME rule (<see cref="ReviewerSeparationPolicy.MissingModel"/>),
  ///     so the two boundaries cannot drift into different spellings.</para></summary>
  private static DomainError? ReviewerWithoutModel(IReadOnlyList<ChainStepSeed> steps)
  {
    foreach (ChainStepSeed step in steps.OrderBy(step => step.Position))
    {
      if (ReviewerSeparationPolicy.MissingModel(step.Role, step.Position) is { } refusal)
      {
        return refusal;
      }
    }

    return null;
  }

  /// <summary>D10: a chain with no executable step is refused. This is the check that stops
  ///     an empty chain from being created and immediately reporting Completed.</summary>
  private static DomainError? NoExecutableSteps(IReadOnlyList<ChainStepSeed> steps) =>
      steps.Count > 0
        ? null
        : new DomainError(ChainCreationCode.NoExecutableSteps,
            "a chain needs at least one executable step; this declaration names none. "
            + "Mark the plan's steps and name the ones that execute, or leave the plan as a plan.");

  /// <summary>Every declared position must exist in the plan. The message names BOTH the
  ///     missing positions and what the plan does have, because a session told only 'no'
  ///     guesses at a position it cannot see.</summary>
  private static DomainError? StepMissing(IReadOnlyList<ChainStepSeed> steps,
      IReadOnlyList<int> planStepPositions)
  {
    HashSet<int> available = [.. planStepPositions];
    List<int> missing = [.. steps.Select(step => step.Position).Distinct().Where(p => !available.Contains(p)).Order()];
    return missing.Count == 0
        ? null
        : new DomainError(ChainCreationCode.StepMissing,
            $"the plan has no step at position(s) {string.Join(", ", missing)}; "
            + $"it has {planStepPositions.Count} step(s)"
            + PositionsClause(planStepPositions));
  }

  /// <summary>The plan's positions as a sentence tail: a period when it has none, else the
  ///     ordered list. Extracted so the refusal reads as one message rather than as a nested
  ///     ternary inside a string concatenation.
  ///     <para>A plan with no steps and a plan whose steps are all excluded are the same
  ///     message here on purpose: the caller learns the plan has nothing to execute, which is
  ///     the fact that changes what they do next.</para></summary>
  private static string PositionsClause(IReadOnlyList<int> planStepPositions) =>
      planStepPositions.Count == 0
          ? "."
          : $" at position(s) {string.Join(", ", planStepPositions.Order())}.";

  /// <summary>One position, one role. A duplicate that AGREES is harmless and collapses; a
  ///     duplicate that disagrees is a contradiction, because two roles for one step is a
  ///     chain nobody can start correctly.</summary>
  private static DomainError? ConflictingRole(IReadOnlyList<ChainStepSeed> steps)
  {
    IGrouping<int, ChainStepSeed>? conflict = steps
        .GroupBy(step => step.Position)
        .FirstOrDefault(group => group.Select(step => step.Role).Distinct().Count() > 1);
    return conflict is null
        ? null
        : new DomainError(ChainCreationCode.ConflictingRole,
            $"step position {conflict.Key} was declared with two different roles "
            + $"({string.Join(" and ", conflict.Select(step => step.Role.Kind).Distinct())}); "
            + "a chain step has exactly one role.");
  }
}
