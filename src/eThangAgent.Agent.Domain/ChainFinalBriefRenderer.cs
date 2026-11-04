namespace eThangAgent.AgentDomain;

/// <summary>Renders the final agent's brief: the chain is complete, so this session summarizes
///     the work done and hands control back to the user. Pure, like <see cref="ChainBriefRenderer"/>:
///     the same facts always render the same text.</summary>
public static class ChainFinalBriefRenderer
{
  /// <summary>The verbatim first line of a final brief. Distinct from the step brief's prefix:
  ///     the final agent executes no step, and a prefix that misstated its provenance is a lie
  ///     the model would reason from.</summary>
  public const string Prefix =
      "[Chain complete — this session summarizes the finished work and hands back to the user.]";

  /// <summary>Renders the final brief for <paramref name="facts"/>.</summary>
  /// <param name="facts">The gathered facts, with <see cref="ChainBriefFacts.IsFinal"/> set.</param>
  public static ChainBrief Render(ChainBriefFacts facts)
  {
    ArgumentNullException.ThrowIfNull(facts);
    return new ChainBrief(Text(facts));
  }

  /// <summary>The brief text. Sections appear in a fixed order so the same facts always
  ///     produce byte-identical output.</summary>
  private static string Text(ChainBriefFacts facts)
  {
    List<string> lines =
    [
      Prefix,
      "",
      $"chain #{facts.ChainId} is complete. Every step has finished.",
      "",
      "## The plan's goal (written by the session that planned this work)",
      "",
      facts.PlanGoal.Trim(),
      "",
      "## Summary of the work done",
      "",
    ];
    foreach (ChainBriefPredecessor predecessor in facts.Predecessors)
    {
      lines.Add($"- Step {predecessor.Position} — {predecessor.Status}: "
          + (string.IsNullOrWhiteSpace(predecessor.Outcome) ? "(no outcome recorded)" : predecessor.Outcome.Trim()));
    }

    lines.AddRange(
    [
      "",
      "## Read before you start",
      "",
      $"- The full plan, including every step and its status: plan.show id={facts.PlanId}",
      $"- This chain's ledger: state.get key=\"{facts.LedgerKey}\"",
      "",
      "## Retrospective",
      "",
      "Write a summary of the work done and a retrospective of how the agent itself handled the session.",
      "Evaluate bugs in behavior, gaps in the tool surface, and missing workflow support, naming a capability, not a one-off fix.",
      "Place the work against the project's overall goal, guiding philosophy, and architecture principles, naming the concrete site wherever the work drifted.",
      "",
      "## How to finish",
      "",
      "Present the summary and the retrospective, then hand control back to the user. Do not mark any step done:",
      "the chain is already complete, and there is no step left to complete.",
    ]);
    return string.Join('\n', lines);
  }
}
