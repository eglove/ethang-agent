using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Port the Tool Domain owns for the workspace-visible refutation namespace behind the
///     experiment tool (issue #315).
///     <para>WHY A SECOND PORT RATHER THAN A METHOD ON <see cref="IExperimentLedgerStore"/>: that
///     port is append-only BY CONTRACT, and a refutation must be a WRITE - recording one twice
///     has to land the same bytes rather than duplicate the record. Adding an overwrite method
///     to the ledger port would have quietly withdrawn the guarantee every ledger writer relies
///     on; a separate port keeps each contract true. The same argument the read side settled on
///     (<see cref="IExperimentLedgerAccess"/> vs the store): two ports with one job each, never
///     one interface with a method a caller must remember not to use.</para>
///     <para>WORKSPACE SCOPE IS THE IMPLEMENTATION'S. The port takes a key, not a workspace id:
///     the composition root adapts it to a store that is already bound to one workspace, so a
///     refutation written by a chain is visible to every other chain in that workspace and to
///     nothing outside it.</para></summary>
public interface IRefutationStore
{
  /// <summary>Writes <paramref name="value"/> to <paramref name="key"/>, replacing whatever
  ///     was there. Returns the new version.
  ///     <para>REPLACING RATHER THAN APPENDING is what makes a re-record idempotent: a refuted
  ///     prediction is judged exactly once, so the value rendered for a given (slug, prediction
  ///     id) is stable, and a retry after a partial failure converges instead of accumulating
  ///     copies.</para></summary>
  Task<Result<int>> WriteAsync(string key, string value, CancellationToken ct = default);
}
