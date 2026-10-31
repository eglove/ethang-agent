namespace eThangAgent.ToolDomain;

/// <summary>What kind of act a <see cref="ChainFloorRule"/> refuses. The kind exists because the
///     three are refused for DIFFERENT reasons even though all of them park a chain, and a
///     single 'forbidden' would leave a reader unable to tell a force-push from a release
///     publication.</summary>
public enum ChainFloorRuleKind
{
  /// <summary>The act rewrites or discards history other work may already rest on.</summary>
  HistoryRewrite,

  /// <summary>The act publishes something outside this machine, where undoing it is not ours
  ///     to do.</summary>
  Publication,

  /// <summary>The act writes outside the workspace the chain is confined to, so nothing in
  ///     the record describes what it changed.</summary>
  OutOfWorkspace,
}

/// <summary>One entry in the universal floor (issue #304): what it refuses, why no gate can
///     license it, and the exact strings that recognize it.</summary>
/// <param name="Id">The stable id a refusal names, so a reader and a test cite one token.
///     </param>
/// <param name="Kind">Which of the three reasons this rule refuses for.</param>
/// <param name="Why">The sentence stating why no green gate licenses the act. Rendered
///     verbatim in the refusal, because 'not allowed' with no reason invites a retry.
///     </param>
/// <param name="Tokens">The substrings whose presence marks a command line as this rule's
///     act. Matched case-insensitively. An EMPTY list means the rule is not recognized from a
///     command line at all, which is deliberate for the workspace-escape entry.</param>
public sealed record ChainFloorRule(string Id, ChainFloorRuleKind Kind, string Why,
    IReadOnlyList<string> Tokens);

/// <summary>THE FLOOR: the acts a chain may never perform, whoever approved them (issue #304,
///     D4). The gate is the OTHER half of the floor, and the two are not interchangeable.
///     <para>THE GATE ANSWERS 'IS THE WORK FINISHED'. This answers 'is this act permitted at
///     all', and the answer is NO regardless of the gate: no amount of passing tests makes a
///     force-push acceptable, and a chain that treated a green gate as a licence would be
///     exactly the failure this list exists to prevent.</para>
///     <para>WHY A NAMED CONSTANT AND NOT A SETTING (D4): a configurable floor is one a chain
///     can be configured past, which is the same as having none. Widening it is a product
///     decision that updates <see cref="Rules"/> and its test in the same change - the shape
///     <c>ChainRetryPolicy.MaxAttempts</c> and <c>ChainReviewPolicy.MaxReviewAttempts</c> take
///     for their bounds.</para>
///     <para>RECOGNITION IS DELIBERATELY COARSE. The harness matches SUBSTRINGS in a command
///     line rather than parsing shells, because parsing is how a check becomes something a
///     rewrite defeats: <c>git push --force-with-lease</c>, <c>git push -f</c> and
///     <c>git push origin +main</c> are three spellings of one act, and a parser that
///     understood all three would still miss the fourth. A false positive parks a chain for a
///     human to release, which is the cheap direction; a false negative lands the act.</para>
///     <para>THE OUT-OF-WORKSPACE ENTRY CARRIES NO TOKENS ON PURPOSE. A workspace escape is a
///     fact about a RESOLVED PATH, not about a command line: the file tools already refuse it
///     with <c>PathOutsideWorkspace</c> and <c>TrustedRootReadOnly</c>, and a token list here
///     would only recognize the shapes somebody thought of. The entry still exists because the
///     floor must SAY that escape is on it - an omitted rule reads as a permitted act.</para>
///     <para>THE WIDENING RULE: an entry is added only when a reviewer can name the act and
///     say why no passing test would license it. 'This seems risky' is what the completion gate
///     and the review step are for.</para></summary>
public static class ChainFloorRules
{
  /// <summary>What the floor refuses, in the order a refusal reads them. NAMED AND PINNED:
  ///     <c>ChainFloorTests</c> asserts this exact set, so adding a rule is a deliberate edit to
  ///     a test as well as to the constant (D4).</summary>
  public static IReadOnlyList<ChainFloorRule> Rules { get; } =
  [
      new("force-push", ChainFloorRuleKind.HistoryRewrite,
          "it rewrites published history that other work may already rest on, and no passing "
          + "gate makes that recoverable for anyone who already pulled it",
          ["push --force", "push -f", "push --mirror", "--force-with-lease", "push origin +"]),
      new("history-rewrite", ChainFloorRuleKind.HistoryRewrite,
          "it rewrites this branch's history rather than adding to it, so a reviewer can no "
          + "longer see what actually happened",
          ["commit --amend", "filter-branch", "filter-repo", "rebase -i",
           "rebase --interactive", "reset --hard"]),
      new("publish", ChainFloorRuleKind.Publication,
          "it publishes outside this machine, where undoing it is not ours to do",
          ["gh release create", "npm publish", "dotnet nuget push", "cargo publish",
           "docker push", "twine upload"]),
      new("out-of-workspace-write", ChainFloorRuleKind.OutOfWorkspace,
          "it writes outside the workspace this chain is confined to, so nothing in the record "
          + "describes what it changed",
          []),
  ];

  /// <summary>The ids, for a refusal that wants to name the whole list. Never empty.</summary>
  public static IReadOnlyList<string> Ids { get; } = [.. Rules.Select(rule => rule.Id)];

  /// <summary>The rule <paramref name="command"/> trips, or null when it trips none.
  ///     <para>FIRST MATCH WINS and the order is <see cref="Rules"/>'s, so a command that trips
  ///     two rules is refused for the first one - the refusal names one act, and a reader
  ///     fixing it does not need the rest.</para></summary>
  /// <param name="command">The command line about to run.</param>
  public static ChainFloorRule? TripFor(string command)
  {
    return string.IsNullOrWhiteSpace(command)
      ? null
      : Rules.FirstOrDefault(rule => rule.Tokens.Any(token =>
          command.Contains(token, StringComparison.OrdinalIgnoreCase)));
  }

  /// <summary>The refusal sentence for a tripped rule. Names the act, why no gate licenses it,
  ///     and both exits - because every refusal names its exit (D3), and the exits here are the
  ///     two the gate's own refusals name: a human proceeds, or the chain is dropped.</summary>
  /// <param name="rule">The tripped rule.</param>
  /// <param name="command">The command line, echoed so the reader sees what tripped it.</param>
  public static string Refusal(ChainFloorRule rule, string command)
  {
    ArgumentNullException.ThrowIfNull(rule);
    ArgumentNullException.ThrowIfNull(command);
    return $"[chain floor] '{command.Trim()}' is refused as {rule.Id}: {rule.Why}. No passing gate "
        + "licenses it, so the chain is PARKED for a human rather than continuing. To proceed, a "
        + "human runs the act themselves and resumes the chain; to drop the work instead, set the "
        + "chain Abandoned - that is always allowed and is the honest exit when the work is not "
        + "finished.";
  }
}
