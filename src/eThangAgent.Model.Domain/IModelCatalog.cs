using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Provides a provider's model catalog entries.</summary>
public interface IModelCatalog
{
  Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default);

  /// <summary>Revalidates against the source, joining an in-flight refresh. A caller's
  /// cancellation cancels only its wait; cached data survives a refresh failure.</summary>
  Task<Result<IReadOnlyList<ModelProviderEntry>>> RefreshAsync(CancellationToken ct = default)
      => GetAsync(ct);
}
