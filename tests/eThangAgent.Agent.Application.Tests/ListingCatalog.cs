using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Test catalog carrying an explicit (model, provider) listing, so a test can state
///     exactly which providers offer which models. A null listing is the FAILED-fetch shape
///     (the Result error), which the pinned-provider rule must read as unverifiable rather
///     than as a vanished provider.</summary>
internal sealed class ListingCatalog(params (string ModelId, string ProviderName)[] pairs) : IModelCatalog
{
  public const string FailureCode = "CatalogUnavailable";

  /// <summary>A catalog whose fetch failed: the Result error shape.</summary>
  public static ListingCatalog Unavailable() => new() { Failed = true };

  private bool Failed { get; init; }

  public Task<Result<IReadOnlyList<ModelProviderEntry>>> GetAsync(CancellationToken ct = default)
      => Failed
          ? Task.FromResult(Result.Failure<IReadOnlyList<ModelProviderEntry>>(
              new DomainError(FailureCode, "the catalog could not be fetched.")))
          : Task.FromResult(Result.Success<IReadOnlyList<ModelProviderEntry>>(
              [.. pairs.Select(p => new ModelProviderEntry(
                  p.ModelId, p.ProviderName, 0.000001m, null, 0.000002m, 128_000, 8_192,
                  SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
                  null, null, null, null, null, null))]));
}
