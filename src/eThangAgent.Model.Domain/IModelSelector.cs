using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Selects the best model+provider pair under user constraints. Options
/// arrive per call: the session resolver passes the carrier's snapshot, the Desktop
/// preview its live draft.</summary>
public interface IModelSelector
{
  Task<Result<ModelSelectionResult>> SelectAsync(
      SmartSelectionOptions options, bool requireVision, CancellationToken ct = default);
}
