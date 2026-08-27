using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Selects the best model for a given task prompt via a two-stage LLM pipeline.</summary>
public interface IModelSelector
{
  Task<Result<ModelSelectionResult>> SelectAsync(string taskPrompt, CancellationToken ct = default);
}
