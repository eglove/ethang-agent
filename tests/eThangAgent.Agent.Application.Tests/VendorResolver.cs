using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>A vendor-keyed resolver over a fixed set of catalogs, so a test can state which
///     vendor serves which models - and which vendors this container wired at all.
///     <para>AN UNLISTED VENDOR IS THE NO-TRANSPORT SHAPE: the resolution fails with the
///     provider's own named error, which is what a container built without that vendor's
///     credential does.</para></summary>
internal sealed class VendorResolver(params (string Vendor, IModelCatalog Catalog)[] catalogs)
    : IModelProviderResolver
{
  /// <summary>A resolver over one vendor's catalog - the single-vendor shape.</summary>
  public static VendorResolver For(string vendor, IModelCatalog catalog) => new((vendor, catalog));

  /// <summary>A resolver over every vendor this build knows, each listing the same catalog.
  ///     The shape a test uses when the vendor does not matter.</summary>
  public static VendorResolver ForEveryVendor(IModelCatalog catalog) =>
      new([.. ModelVendors.All.Select(vendor => (vendor, catalog))]);

  public Result<IModelCatalog> Catalog(string vendorId)
      => catalogs.FirstOrDefault(pair => pair.Vendor == vendorId).Catalog is { } found
          ? Result.Success(found)
          : Result.Failure<IModelCatalog>(new DomainError("ProviderNotConfigured",
              $"provider '{vendorId}' has no catalog in this session."));

  public Result<IModelProvider> Resolve(string vendorId, ModelConfig config)
      => Result.Failure<IModelProvider>(new DomainError("ProviderNotConfigured",
          $"provider '{vendorId}' has no transport in this session."));
}
