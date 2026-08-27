using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Selects the best model+provider pair for a given task prompt via a two-stage LLM pipeline.</summary>
public interface IModelSelector
{
  Task<Result<ModelSelectionResult>> SelectAsync(
      string taskPrompt,
      IReadOnlySet<string>? excludedKeys = null,
      CancellationToken ct = default);
}
