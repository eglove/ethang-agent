namespace eThangAgent.Desktop.ViewModels;

/// <summary>Where one agent sits in the chain it executes (issue #329): the chain it belongs to,
///     that chain's title, its 1-based position, and how many steps the chain has.
///     <para>WHY THE CELL CARRIES IT: <c>chain_steps.session_id</c> records the link between a
///     session and the step it runs, and without it five cells of one chain read as five
///     unrelated conversations rather than five steps of one piece of work.</para>
///     <para>A HAND-STARTED AGENT HAS NONE. That absence is the fact the cell renders as
///     'nothing extra', not an empty placement object that would render as a blank line.</para></summary>
/// <param name="ChainId">The chain the agent's step belongs to.</param>
/// <param name="ChainTitle">The chain's title, which is the plan's title at creation.</param>
/// <param name="Position">The 1-based position of the step this agent runs.</param>
/// <param name="Total">How many steps the chain has, or null when the chain record could not
///     be read - the position is then stated without a total rather than with a wrong one.</param>
internal sealed record ChainPlacement(int ChainId, string ChainTitle, int Position, int? Total)
{
  /// <summary>True when this placement is the FINAL agent's: the chain is complete, so the cell
  ///     states the chain with no step of its own.</summary>
  public bool IsFinal => Position == 0;

  /// <summary>The placement line the cell header renders: 'step 2 of 5' followed by the chain's
  ///     title when it has one. The final agent states 'chain complete' instead: it executes no
  ///     step, so a step position would be a lie about what it is.</summary>
  public string Line => IsFinal
    ? FinalLine()
    : (Total, string.IsNullOrWhiteSpace(ChainTitle)) switch
    {
      ({ } total, true) => $"step {Position} of {total}",
      ({ } total, false) => $"step {Position} of {total} · {ChainTitle}",
      (null, true) => $"step {Position}",
      (null, false) => $"step {Position} · {ChainTitle}",
    };

  private string FinalLine() => string.IsNullOrWhiteSpace(ChainTitle) ? "chain complete" : $"chain complete · {ChainTitle}";
}
