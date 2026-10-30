using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Renders the pointer brief a chain step's session is seeded with (issue #302).
///     <para>D1 — A POINTER, NOT A PAYLOAD. The brief names the plan, the step, and the record
///     keys; the session reads them itself. Inlining the plan would freeze it at launch and
///     re-send it on every iteration, and a plan revision would never reach the step.</para>
///     <para>D2 — THE DETERMINISTIC HALF IS RENDERED, so it cannot be omitted by a planner
///     that forgot and is identical every time for the same record.</para>
///     <para>D3 — THE MODEL-AUTHORED HALF IS THE PLAN ITSELF. The plan's goal and the step's
///     detail are both written by the model that planned; there is no separate notes artifact
///     and none is invented here. The goal is carried VERBATIM because it is the one thing a
///     renderer cannot invent.</para>
///     <para>D4 — THE COMPLETION CONTRACT IS NAMED EXPLICITLY: which step to mark, that the
///     harness will not accept a different one, and that the gate must be fresh.</para>
///     <para>D5 — A BLANK BRIEF IS A REFUSAL, not an empty session. See
///     <see cref="RefusalFor"/>: a chain step whose plan step is gone is a plan defect, and
///     starting a session on it produces work nobody asked for.</para>
///     <para>PURE: the same facts always render the same text. No clock, no I/O, no counter —
///     which is what lets a test pin the brief and what stops a resume from re-rendering
///     something different from the launch.</para></summary>
public static class ChainBriefRenderer
{
  /// <summary>The verbatim first line of a chain brief. Distinct from the compaction
  ///     handoff's prefix (<c>SessionHandoffSeed.Prefix</c>): a chain step is not continuing
  ///     an earlier conversation, it is executing an assigned step, and a prefix that
  ///     misstated its own provenance is a lie the model would reason from.</summary>
  public const string Prefix =
      "[Chain step brief — this session executes one assigned step of a plan.]";

  /// <summary>Renders the brief for <paramref name="facts"/>, or the named refusal when the
  ///     facts describe a step that cannot be started.</summary>
  /// <param name="facts">The gathered facts.</param>
  public static Result<ChainBrief> Render(ChainBriefFacts facts)
  {
    ArgumentNullException.ThrowIfNull(facts);
    return RefusalFor(facts) is { } refusal
      ? Result.Failure<ChainBrief>(refusal)
      : Result.Success(new ChainBrief(Text(facts, facts.AssignedStep!)));
  }

  /// <summary>The refusal a step's facts earn, or null when they describe a startable step.
  ///     <para>THE ONE REFUSAL IS A MISSING PLAN STEP. Chains bind to plan steps by position,
  ///     and removing a step renumbers the tail, so this is the defect that actually bites:
  ///     the chain would otherwise start a session on work that no longer exists and the
  ///     session would have nothing to complete.</para></summary>
  /// <param name="facts">The gathered facts.</param>
  public static DomainError? RefusalFor(ChainBriefFacts facts)
  {
    ArgumentNullException.ThrowIfNull(facts);
    return facts.AssignedStep is not null
      ? null
      : new DomainError("ChainStepMissing",
          $"chain #{facts.ChainId} step {facts.AssignedPosition} points at plan #{facts.PlanId} "
          + $"position {facts.AssignedPosition}, but that plan has {facts.PlanSteps.Count} step(s) "
          + "and none at that position. The plan was revised after the chain started; "
          + "re-create the chain against the current plan.");
  }

  /// <summary>The brief text. Sections appear in a fixed order so the same facts always
  ///     produce byte-identical output.</summary>
  private static string Text(ChainBriefFacts facts, ChainBriefPlanStep step)
  {
    List<string> lines =
    [
      Prefix,
      "",
      $"You are step {facts.AssignedPosition} of chain #{facts.ChainId}, executing plan #{facts.PlanId}.",
      "",
      "## Your step",
      "",
      $"Position {step.Position}: {step.Title}",
    ];

    if (!string.IsNullOrWhiteSpace(step.Detail))
    {
      lines.Add("");
      lines.Add(step.Detail.Trim());
    }

    lines.AddRange(
    [
      "",
      "## The plan's goal (written by the session that planned this work)",
      "",
      facts.PlanGoal.Trim(),
      "",
      "## Read before you start",
      "",
      $"- The full plan, including every step and its status: plan.show id={facts.PlanId}",
      $"- This chain's ledger: state.get key=\"{facts.LedgerKey}\"",
      "",
      "The ledger is CHAIN scope: every step of this chain reads and writes the same key, so a",
      "refutation recorded by an earlier step is yours to read. An empty ledger is normal — this",
      "may be the first step. Record observations, verdicts, and refutations AS YOU WORK, not",
      "only at the end: a refutation recorded after your session ends helps nobody.",
    ]);

    AddPredecessors(lines, facts);
    AddGate(lines, facts);
    // THE ONE BRANCH ON ROLE (issue #306): a reviewer is not asked to finish the work, because
    // a reviewer told to mark the step done would finish it instead of judging it.
    if (facts.IsReview)
    {
      AddReview(lines, facts);
    }
    else
    {
      AddCompletion(lines, facts);
    }

    return string.Join('\n', lines);
  }

  /// <summary>What earlier steps concluded, so this one does not re-derive it.</summary>
  private static void AddPredecessors(List<string> lines, ChainBriefFacts facts)
  {
    if (facts.Predecessors.Count == 0)
    {
      return;
    }

    lines.Add("");
    lines.Add("## Steps that already ran");
    lines.Add("");
    foreach (ChainBriefPredecessor predecessor in facts.Predecessors)
    {
      lines.Add($"- Step {predecessor.Position} — {predecessor.Status}: "
          + (string.IsNullOrWhiteSpace(predecessor.Outcome) ? "(no outcome recorded)" : predecessor.Outcome.Trim()));
    }
  }

  /// <summary>The gate commands, or an explicit statement that this workspace declares none.
  ///     An unset gate never blocks (the <c>CompletionGate</c> rule), and saying so is
  ///     different from saying nothing.</summary>
  private static void AddGate(List<string> lines, ChainBriefFacts facts)
  {
    lines.Add("");
    lines.Add("## The gate");
    lines.Add("");
    if (facts.GateCommands.Count == 0)
    {
      lines.Add("This workspace declares no goal/check commands, so no gate is required of you.");
      return;
    }

    lines.Add("Run these and confirm they pass before you claim your step done:");
    lines.Add("");
    foreach (string command in facts.GateCommands)
    {
      lines.Add($"    {command}");
    }

    lines.Add("");
    lines.Add("state.checkgoal runs them and records the result. The result must be FRESH — run it");
    lines.Add("again after your last change, or the completion gate refuses the claim.");
  }

  /// <summary>How a REVIEW step finishes (issue #306): judge the work and record a verdict,
  ///     rather than mark the step done.
  ///     <para>WHY THIS IS NOT <see cref="AddCompletion"/>: a reviewer handed the implementer's
  ///     contract is told to mark the step done, which is the one thing a reviewer must not do —
  ///     it would finish the work instead of judging it, and the chain would advance on the
  ///     reviewer's own labour. The role exists so this instruction can differ.</para>
  ///     <para>THE VERDICT IS NOT A TYPE YET (#216). <see cref="ChainStep.Outcome"/> is free text
  ///     on the record, so the verdict and its cited findings ride the step outcome. This states
  ///     the shape the outcome must have; #216 gives it a typed home.</para>
  ///     <para>THE RUBRIC IS #217's, AND ITS ABSENCE IS STATED. A reviewer told to apply a rubric
  ///     that does not exist would invent one, and an invented rubric is a verdict nobody can
  ///     compare. Saying plainly that none is installed is the honest answer, and it is the reason
  ///     <see cref="ChainBriefFacts.ReviewRubricAvailable"/> is a fact rather than an assumption.</para></summary>
  private static void AddReview(List<string> lines, ChainBriefFacts facts)
  {
    lines.Add("");
    lines.Add("## You are the REVIEWER for this step");
    lines.Add("");
    // The word is literal because this method only runs for a review step (the branch in
    // Text), so there is no second case for a computed name to distinguish.
    lines.Add($"Step {facts.AssignedPosition} was assigned the review role, so your job is to JUDGE the");
    lines.Add("work on it, not to do it. Do not change the workspace to make the work pass: a reviewer");
    lines.Add("that fixes what it reviews has replaced the independent verdict with its own.");
    lines.Add("");
    AddRubric(lines, facts);
    lines.Add("## What to record");
    lines.Add("");
    lines.Add("Record your verdict with plan.review before you mark the step done:");
    lines.Add("");
    lines.Add("    verdict: approve | deny | approve-with-conditions");
    lines.Add("    scores: requirement-coverage, testability, blast-radius (each 1-5, with a reason)");
    lines.Add("    findings: one per finding, each citing a requirement id or a step position");
    lines.Add("");
    lines.Add("The harness reads the RECORD, not your transcript: a verdict that stays in prose moves");
    lines.Add("nothing, and a review step that completes without one is refused.");
    lines.Add("");
    lines.Add("A finding that cites nothing is not a finding: 'the code is unclear' cannot be acted on or");
    lines.Add("audited, while 'step 3 has no acceptance criterion for requirement R2' can.");
    lines.Add("");
    lines.Add("## How to finish");
    lines.Add("");
    lines.Add($"Mark step {facts.AssignedPosition} of plan #{facts.PlanId} done once your verdict is recorded:");
    lines.Add("");
    lines.Add($"    plan.update-step id={facts.PlanId} position={facts.AssignedPosition} status=Done");
    lines.Add("");
    lines.Add("That is the ONLY step you may mark done. The harness refuses a Done on any other step of");
    lines.Add("this plan, and it refuses a Done on this step from any other session.");
    lines.Add("");
    lines.Add("Completing your step is what starts the next one — the harness reacts to it, you do not");
    lines.Add("start anything yourself. If you cannot reach a verdict, say so in the transcript and stop;");
    lines.Add("the chain parks rather than guessing.");
  }

  /// <summary>The rubric a reviewer applies: the skill when it is installed, and an explicit
  ///     statement when it is not. Never an invented rubric — an unstated one is at least
  ///     visibly absent, while a fabricated one is a verdict nobody can compare or audit.</summary>
  private static void AddRubric(List<string> lines, ChainBriefFacts facts)
  {
    lines.Add("## The rubric");
    lines.Add("");
    if (facts.ReviewRubricAvailable)
    {
      lines.Add("Load the `reviewing-plans` skill and apply its rubric: it carries the dimensions to score,");
      lines.Add("the verdict contract, and the citation rule. It is the rubric for this review.");
    }
    else
    {
      lines.Add("No `reviewing-plans` skill is installed in this session, so there is no rubric to apply.");
      lines.Add("Judge the work against the plan's own goal and its steps, and say in your verdict that no");
      lines.Add("rubric was available. A verdict that silently invents its own criteria cannot be compared");
      lines.Add("with any other review.");
    }

    lines.Add("");
  }

  /// <summary>D4: how to finish, stated so a session that does not know how to finish can.</summary>
  private static void AddCompletion(List<string> lines, ChainBriefFacts facts)
  {
    lines.Add("");
    lines.Add("## How to finish");
    lines.Add("");
    lines.Add($"Mark step {facts.AssignedPosition} of plan #{facts.PlanId} done:");
    lines.Add("");
    lines.Add($"    plan.update-step id={facts.PlanId} position={facts.AssignedPosition} status=Done");
    lines.Add("");
    lines.Add("That is the ONLY step you may mark done. The harness refuses a Done on any other step");
    lines.Add("of this plan, and it refuses a Done on this step from any other session.");
    lines.Add("");
    lines.Add("Completing your step is what starts the next one — the harness reacts to it, you do not");
    lines.Add("start anything yourself. If you cannot finish, say so in the transcript and stop; the");
    lines.Add("chain parks rather than guessing.");
  }
}
