using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition;

/// <summary>Resolves a provider and a catalog from the VENDOR id a selection entry names,
///     over the transports this container registered.
///     <para>THE CONTAINER HOLDS EVERY VENDOR IT WAS WIRED WITH, so a session whose recorded
///     binding names one vendor still serves a model another vendor provides. The recorded
///     binding stays the session's identity; it no longer decides which provider exists.
///     </para>
///     <para>A VENDOR WITH NO TRANSPORT IS A NAMED FAILURE. The usual cause is a credential
///     that was never configured or was cleared, and the caller reports that fact instead of
///     crashing on a missing registration.</para>
/// </summary>
/// <param name="services">The container whose registrations answer the resolution.</param>
public sealed class ProviderResolver(IServiceProvider services) : IModelProviderResolver
{
  /// <summary>The error code a vendor with no transport reports.</summary>
  public const string ProviderNotConfigured = "ProviderNotConfigured";

  private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

  /// <inheritdoc />
  public Result<IModelProvider> Resolve(string vendorId, ModelConfig config)
  {
    if (!ModelVendors.IsKnown(vendorId))
    {
      return Result.Failure<IModelProvider>(new DomainError("UnknownProvider",
          $"Unknown provider '{vendorId}'. Known providers: {string.Join(", ", ModelVendors.All)}."));
    }

    ArgumentNullException.ThrowIfNull(config);
    IModelProviderFactory? factory = _services.GetKeyedService<IModelProviderFactory>(vendorId);
    return factory is null
        ? Result.Failure<IModelProvider>(Unavailable<IModelProvider>(vendorId))
        : Result.Success(factory.Create(config));
  }

  /// <inheritdoc />
  public Result<IModelCatalog> Catalog(string vendorId)
  {
    if (!ModelVendors.IsKnown(vendorId))
    {
      return Result.Failure<IModelCatalog>(new DomainError("UnknownProvider",
          $"Unknown provider '{vendorId}'. Known providers: {string.Join(", ", ModelVendors.All)}."));
    }

    IModelCatalog? catalog = _services.GetKeyedService<IModelCatalog>(vendorId);
    return catalog is null
        ? Result.Failure<IModelCatalog>(Unavailable<IModelCatalog>(vendorId))
        : Result.Success(catalog);
  }

  /// <summary>The refusal for a vendor this container registered no transport for.</summary>
  private static DomainError Unavailable<T>(string vendorId) => new(ProviderNotConfigured,
      $"Provider '{ModelVendors.DisplayName(vendorId)}' has no {typeof(T).Name} in this session: "
      + "its credential is not configured. Complete its settings under Settings (gear icon) "
      + "and open the agent again.");
}
