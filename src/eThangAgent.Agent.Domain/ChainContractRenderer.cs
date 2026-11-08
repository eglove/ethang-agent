namespace eThangAgent.AgentDomain;

/// <summary>Renders the chain contract the system prompt carries (issue #311).
///     <para>D1 — WHAT IT SAYS: that this session is one step of a chain, which step, that the
///     harness starts the next session when this one marks its step done, that the chain's
///     ledger is shared across every step, and that a done-claim must be backed by a
///     verification run newer than the last change. Those are the four facts a session cannot
///     infer from its tool list.</para>
///     <para>D4 — IT DOES NOT RESTATE THE SKILL. The chain skill (#308) carries the PROCEDURE —
///     how to plan, how to sequence, how to recover. This carries the IDENTITY, which is what a
///     model needs before it decides whether to load a skill at all. Two statements of one rule
///     drift, and the prompt is the worse place to keep the copy: it is in every request, so a
///     stale sentence is re-read on every iteration.</para>
///     <para>IT DOES NOT REPEAT THE BRIEF EITHER. The plan's goal, the step's title and detail,
///     and the predecessors are the brief's, and it is already in the conversation where it is
///     paid for once. What the prompt adds is the STANDING rule — the completion contract and
///     the ledger's scope — which the brief states once and the model must keep applying after
///     the first message scrolls out of attention.</para>
///     <para>PURE: the same contract always renders the same text. No clock, no I/O, no
///     counter — the prompt is the first bytes of every request, so a drifting sentence would
///     invalidate the provider's prompt cache from its first changed character (issue #267).</para></summary>
public static class ChainContractRenderer
{
  /// <summary>The verbatim first line, so a test and a reader can find the section without
  ///     depending on the rest of the text.</summary>
  public const string Prefix = "[Chain contract — this session executes one step of a chain.]";

  /// <summary>Renders the contract for <paramref name="contract"/>.</summary>
  /// <param name="contract">The chain facts this session runs under.</param>
  public static string Render(ChainContract contract)
  {
    ArgumentNullException.ThrowIfNull(contract);
    List<string> lines =
    [
      Prefix,
      "",
      $"You are step {contract.Position} of chain #{contract.ChainId}, executing plan #{contract.PlanId}.",
      "The harness started this session for that step and will start the next session when you mark",
      "it done. Nothing else decides whether the chain continues: not you, and not a parent agent.",
      "",
      "## The ledger is chain scope",
      "",
      $"Every step of this chain reads and writes the same ledger: state.get key=\"{contract.LedgerKey}\"",
      "Record observations, verdicts, and refutations AS YOU WORK, not only at the end. A refutation",
      "recorded after your session ends helps nobody, and a later step that re-derives what you",
      "already established has wasted the chain's time.",
      "",
    ];
    AddCompletionEvidence(lines);
    // THE COMPLETION RULE DIFFERS BY ROLE (issue #306), and this is where the STANDING rule
    // lives — the brief states it once and a compaction can evict it, so a reviewer that read
    // the right instruction only in its first message would drift back to finishing the work.
    if (contract.IsReview)
    {
      AddReview(lines, contract);
    }
    else
    {
      AddCompletion(lines, contract);
    }

    return string.Join('\n', lines);
  }

  /// <summary>What backs a done-claim: a verification run the session chose itself, started
  ///     after its last change.</summary>
  private static void AddCompletionEvidence(List<string> lines)
  {
    lines.Add("## What backs a done-claim");
    lines.Add("");
    lines.Add("No verification command is configured for this workspace, so you choose it: run whatever");
    lines.Add("actually verifies this work and confirm it exits 0. Any command that exits 0 counts,");
    lines.Add("whatever the project type.");
    lines.Add("");
    lines.Add("The completion gate reads your session's shell ledger. A result from before your last");
    lines.Add("edit does not count: it proves nothing about the code being declared finished, and the");
    lines.Add("gate refuses a claim that predates the change.");
    lines.Add("");
  }

  /// <summary>The completion rule for a REVIEW step (issue #306): judge, record a verdict,
  ///     then mark the step done. The implementer's rule is the wrong instruction for a
  ///     reviewer — it asks for the work to be finished rather than judged.
  ///     <para>IT DOES NOT RESTATE THE BRIEF'S OUTPUT SHAPE. The verdict line and the citation
  ///     rule are the brief's (and #217's, once the rubric skill exists); this states only that
  ///     the verdict is what the step is FOR, which is the standing fact a session must keep
  ///     applying after its first message scrolls away.</para></summary>
  private static void AddReview(List<string> lines, ChainContract contract)
  {
    lines.Add("## How the chain moves");
    lines.Add("");
    // The word is literal because this method only runs for a review step (the branch in
    // Render), so there is no second case for a computed name to distinguish.
    lines.Add($"Step {contract.Position} was assigned the review role: your job is to JUDGE this");
    lines.Add("step's work, not to do it. Do not change the workspace to make the work pass.");
    lines.Add("");
    // THE VERDICT GOES IN THE RECORD, NOT IN FREE TEXT (issue #320). The old sentence told a
    // reviewer to put its verdict in the step's outcome, and the advance settled with
    // outcome: null — so the work was done and nothing kept it. plan.review writes the typed
    // record the cursor reads.
    lines.Add("Record your verdict with plan.review — the verdict, the four rubric scores, and every");
    lines.Add("finding with its citation — then mark the step done. That single write is the completion");
    lines.Add("fact the harness reacts to, and it is the only step of this plan you may mark done.");
    lines.Add("");
    lines.Add("The harness reads your verdict: a denial reopens the step you judged for a fix session");
    lines.Add("seeded with your findings, and an approve-with-conditions carries those conditions into");
    lines.Add("the next step's brief.");
    lines.Add("");
    lines.Add("If you cannot reach a verdict, say so plainly in the transcript and stop. The chain parks");
    lines.Add("rather than guessing, and a parked chain is something a human can act on.");
  }

  /// <summary>The completion rule, stated as a rule rather than as a command: the command
  ///     itself is the brief's, and repeating it here would give the session two copies of an
  ///     argument list that a plan revision could make stale.</summary>
  private static void AddCompletion(List<string> lines, ChainContract contract)
  {
    lines.Add("## How the chain moves");
    lines.Add("");
    lines.Add($"Mark step {contract.Position} of plan #{contract.PlanId} done when — and only when — the work");
    lines.Add("is finished and the gate is fresh. That single write is the completion fact the harness");
    lines.Add("reacts to, and it is the only step of this plan you may mark done.");
    lines.Add("");
    lines.Add("If you cannot finish, say so plainly in the transcript and stop. The chain parks rather");
    lines.Add("than guessing, and a parked chain is something a human can act on.");
  }
}
