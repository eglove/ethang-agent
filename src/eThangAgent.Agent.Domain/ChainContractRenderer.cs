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
    if (contract.IsRefine)
    {
      AddRefine(lines, contract);
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

  /// <summary>The standing rule for a REFINE step (issue #313 D7): make the plan executable,
  ///     then mark the step done. The implementer's rule is the wrong instruction for a refiner —
  ///     it asks for the work to be finished rather than for the plan to be made ready.
  ///     <para>IT DOES NOT RESTATE THE BRIEF. What to sharpen, and how, is the brief's; this
  ///     states only that the plan is what the step is FOR, which is the standing fact a session
  ///     must keep applying after its first message scrolls away.</para>
  ///     <para>NO VERDICT AND NO RUBRIC. A refiner that returned a verdict would be the reviewer
  ///     this role replaced, and a denial would need the back-edge that wedged a chain. It refines
  ///     until it believes the plan is ready and completes; the chain then advances.</para>
  ///     </summary>
  private static void AddRefine(List<string> lines, ChainContract contract)
  {
    lines.Add("## How the chain moves");
    lines.Add("");
    lines.Add($"Step {contract.Position} was assigned the refine role: your job is to make this plan");
    lines.Add("EXECUTABLE, not to implement it. Do not write the feature - the steps after you do that.");
    lines.Add("");
    lines.Add("Change the plan's steps until each one states what must be observably true when it is");
    lines.Add("done. Then mark this step done. That single write is the completion fact the harness");
    lines.Add("reacts to, and it is the only step of this plan you may mark done.");
    lines.Add("");
    lines.Add("Your done-claim is backed by the PLAN, not by a command: no shell run can show that a step");
    lines.Add("is checkable, so the completion gate does not apply to this step. What applies is that the");
    lines.Add("plan actually changed.");
    lines.Add("");
    lines.Add("If the plan cannot be made ready, say so plainly in the transcript and stop. The chain parks");
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
