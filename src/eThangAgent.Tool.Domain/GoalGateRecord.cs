namespace eThangAgent.ToolDomain;

/// <summary>What the last gate run left behind, persisted at the <c>goal/lastcheck</c>
///     state key. <see cref="Digest"/> is the workspace identity AT THE MOMENT the gate
///     ran, which is what makes freshness answerable: comparing it with the current
///     digest is a content comparison, not a clock comparison, so a rewrite that changed
///     nothing does not invalidate a green gate.
///     <para>A null digest is a record written before digest tracking existed, or one
///     taken when the workspace could not be read. It is kept rather than guessed at:
///     the evaluator treats it as unverifiable instead of assuming freshness.</para></summary>
public sealed record GoalGateRecord(string? Digest, bool Passed, DateTimeOffset RanAt)
{
  /// <summary>The persisted form. A corrupt value is the reader's problem to report, so
  ///     this type never parses its own input - see <see cref="CompletionGate.ParseRecord"/>.</summary>
  public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
  {
    digest = Digest,
    passed = Passed,
    ranAt = RanAt,
  });
}
