namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for the fact a refutation is stamped with (issue #315 D7):
///     what workspace state it was found against.
///     <para>WHY A COMMIT AND NOT A CLOCK: code changes; a timer does not track code. "Refuted
///     against abc1234" tells a reader exactly how stale the finding is - and a reader who can
///     see that the commit is far behind HEAD knows to re-test - while a TTL can only say the
///     record is old, never whether the code moved.</para>
///     <para>WHY THIS IS A PORT AND NOT A CALL: the Tool Domain owns no git and no paths, and
///     the composition root is the one place that knows both. It is deliberately a SOURCE OF
///     WORDS rather than a source of hashes, so the decision about how an absent commit is
///     stated lives with the reader that has to live with it.
///     </para></summary>
public interface IRefutationStampSource
{
  /// <summary>Describes what the workspace currently sits at, for a refutation's stamp.
  ///     <para>IT NEVER FAILS. A workspace that is not a git repository, a repository whose
  ///     HEAD is unborn, and a git invocation that could not run all yield
  ///     <see cref="RefutationRecord.NoCommit"/>: a refutation found outside git is still a
  ///     refutation, and refusing to record it would lose the finding in order to keep one
  ///     field tidy. The stamp's job is to say what it was against HONESTLY, and 'nothing to
  ///     name' is the honest answer in those cases.</para></summary>
  Task<string> DescribeAsync(CancellationToken ct = default);
}
