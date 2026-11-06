using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

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
    return facts.IsFinal
      ? Result.Success(ChainFinalBriefRenderer.Render(facts))
      : RenderStep(facts);
  }

  private static Result<ChainBrief> RenderStep(ChainBriefFacts facts)
    => RefusalFor(facts) is { } refusal
      ? Result.Failure<ChainBrief>(refusal)
      : Result.Success(new ChainBrief(Text(facts, facts.AssignedStep!)));

  /// <summary>The refusal a step's facts earn, or null when they describe a startable step.
  ///     <para>THE ONE REFUSAL IS A MISSING PLAN STEP. Chains bind to plan steps by position,
  ///     and removing a step renumbers the tail, so this is the defect that actually bites:
  ///     the chain would otherwise start a session on work that no longer exists and the
  ///     session would have nothing to complete.</para></summary>
  /// <param name="facts">The gathered facts.</param>
  public static DomainError? RefusalFor(ChainBriefFacts facts)
  {
    ArgumentNullException.ThrowIfNull(facts);
    return facts.IsFinal || facts.AssignedStep is not null
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
      "",
      "## Search for refutations before you start",
      "",
      "Refutations are WORKSPACE scope, not chain scope: every one recorded in this workspace sits",
      $"in the `{RefutationRecord.Namespace}/` namespace, and `state.find` searches it. Run",
      "state.find with the terms your step is about before you design anything — a chain that",
      "already disproved this approach spent a whole session learning it, and re-deriving the same",
      "negative result costs the same again.",
      "",
      "You do not write to that namespace yourself. A `record-verdict` with verdict=refuted writes",
      "the shared record for you, in the same call that records the verdict in your ledger.",
    ]);

    AddPredecessors(lines, facts);
    AddSiblings(lines, facts);
    AddReconciler(lines, facts);
    AddCompletionEvidence(lines);
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

  /// <summary>The steps of this step's group that are running at the same moment, so a member
  ///     knows which work is happening beside it and that it must neither change nor wait on it.
  ///     </summary>
  private static void AddSiblings(List<string> lines, ChainBriefFacts facts)
  {
    if (facts.Siblings.Count == 0)
    {
      return;
    }

    lines.Add("");
    lines.Add("## Steps running BESIDE you");
    lines.Add("");
    lines.Add("These steps of the same chain are running at the same moment as yours, each in its own");
    lines.Add("git worktree. Their outcomes do not exist yet. Do not change work that belongs to them,");
    lines.Add("and do not wait for them: the harness merges the branches when every member has finished.");
    lines.Add("");
    foreach (ChainBriefSibling sibling in facts.Siblings)
    {
      lines.Add($"- Step {sibling.Position} — {sibling.Title}");
    }
  }


  /// <summary>What a RECONCILER must merge (D7/D14): the members' branches, the state of the
  ///     workspace checkout it merges into, and the members that did not succeed.
  ///     <para>THE BRANCHES ARE NAMED, NOT SUMMARIZED. The reconciler merges by branch, and a
  ///     brief that said 'two branches' would leave it to guess which, in which directory.</para>
  ///     <para>THE CHECKOUT'S STATE IS TOLD, NEVER REFUSED. An uncommitted checkout is the
  ///     model's to resolve - commit, stash, or work around - so the brief states the fact and
  ///     leaves the decision; a read that FAILED says so rather than claiming the checkout is
  ///     clean, because 'nothing is there' and 'nobody could look' are different facts.</para>
  ///     </summary>
  private static void AddReconciler(List<string> lines, ChainBriefFacts facts)
  {
    if (facts.Reconciliation is not { } reconciliation)
    {
      return;
    }

    lines.Add("");
    lines.Add("## You are the RECONCILER");
    lines.Add("");
    lines.Add("The group before you ran in separate git worktrees. Your job is to merge their branches into");
    lines.Add("this workspace checkout. Nothing else follows until you do.");
    lines.Add("");
    foreach (ChainBriefBranch branch in reconciliation.Branches)
    {
      lines.Add($"- Step {branch.Position} — branch `{branch.Branch}` at {branch.WorktreePath}");
    }

    lines.Add("");
    lines.Add("## The workspace checkout");
    lines.Add("");
    AddCheckoutState(lines, reconciliation);
    lines.Add("");
    lines.Add("## How to merge");
    lines.Add("");
    lines.Add("Merge each branch in the order listed. A conflict is yours to resolve: resolve it, then record");
    lines.Add("what you resolved and why in your outcome before you mark this step done. The review step");
    lines.Add("after you judges the merged result, so an unexplained resolution has nothing behind it.");
    if (reconciliation.FailedPositions.Count > 0)
    {
      lines.Add("");
      lines.Add($"Step(s) {string.Join(", ", reconciliation.FailedPositions)} did not succeed. Merge the branches");
      lines.Add("that did, then stop and say so: the harness parks the chain and names them.");
    }
  }

  /// <summary>The checkout paragraph: clean, dirty with the files named, or unreadable. The three
  ///     are distinct answers and the third is never rendered as the first.</summary>
  private static void AddCheckoutState(List<string> lines, ChainBriefReconciliation reconciliation)
  {
    if (!reconciliation.CheckoutReadable)
    {
      lines.Add("The workspace checkout could not be read; check it yourself before merging.");
      return;
    }

    if (reconciliation.DirtyFiles.Count == 0)
    {
      lines.Add("The workspace checkout is clean.");
      return;
    }

    lines.Add($"The workspace checkout has uncommitted changes: {string.Join(", ", reconciliation.DirtyFiles)}.");
    lines.Add("They are not yours. Commit or stash them before you merge, or work around them - the");
    lines.Add("decision is yours, but do not discard them.");
  }

  /// <summary>What backs a done-claim. The workspace declares no command set, so the session
  ///     chooses its own verification and the harness only asks whether that answer is current.
  ///     </summary>
  private static void AddCompletionEvidence(List<string> lines)
  {
    lines.Add("");
    lines.Add("## What backs your done-claim");
    lines.Add("");
    lines.Add("No verification command is configured for this workspace, so the choice is yours: run");
    lines.Add("whatever actually verifies this work — the build, the tests, the linter, the script —");
    lines.Add("and confirm it exits 0. Any command that exits 0 counts, whatever the project type.");
    lines.Add("");
    lines.Add("The completion gate reads your session's shell ledger, so the passing run must START");
    lines.Add("after your last change. A green run from before your final edit proves nothing about the");
    lines.Add("code you are declaring finished, and the gate refuses a claim that predates the change.");
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

  /// <summary>D4: how to finish, stated so a session that does not know how to finish can.
  ///     <para>A MEMBER OF A GROUP FINISHES DIFFERENTLY IN TWO WAYS: its work must be committed,
  ///     because the harness merges its branch and an uncommitted worktree refuses the
  ///     completion; and its completion starts nothing, because the group is a barrier.</para>
  ///     </summary>
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
    if (facts.IsGroupMember)
    {
      AddMemberCommit(lines);
      lines.Add("Completing your step starts nothing on its own: this step is one of a group that runs");
      lines.Add("together, and the harness starts what follows only once every member has finished. If you");
      lines.Add("cannot finish, say so in the transcript and stop; the chain parks rather than guessing.");
      return;
    }

    lines.Add("Completing your step is what starts the next one — the harness reacts to it, you do not");
    lines.Add("start anything yourself. If you cannot finish, say so in the transcript and stop; the");
    lines.Add("chain parks rather than guessing.");
  }

  /// <summary>The commit duty of a group member: the harness merges its branch into the
  ///     workspace, so the work has to exist on the branch before the step completes.</summary>
  private static void AddMemberCommit(List<string> lines)
  {
    lines.Add("Your work must be COMMITTED before you mark this step done. The harness merges your branch");
    lines.Add("into the workspace, so an uncommitted worktree refuses your completion. When your work is");
    lines.Add("done, run `git add -A` and `git commit -m \"<what you did>\"` in this session's workspace.");
    lines.Add("");
  }
}
