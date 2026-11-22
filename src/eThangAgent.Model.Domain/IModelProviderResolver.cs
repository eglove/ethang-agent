using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain;

/// <summary>Resolves the provider and catalog a VENDOR id names, so a caller holding a
///     vendor-carrying selection entry reaches the right provider without the container being
///     wired for one vendor.
///     <para>A VENDOR WITHOUT A TRANSPORT IS A NAMED FAILURE, not an exception: a container
///     built before a credential existed, or a credential cleared mid-session, is a
///     configuration fact the caller reports rather than a programmer error.</para>
///     <para>THE DECLARATION LIVES HERE because the Agent Domain resolves per attempt and
///     cannot reference the composition root, exactly as <see cref="ModelVendors"/> does.
///     The implementation sits at the composition root, which owns the registrations.</para>
/// </summary>
public interface IModelProviderResolver
{
  /// <summary>The provider that serves <paramref name="vendorId"/>, bound to
  ///     <paramref name="config"/>. Fails with a named error when the vendor has no
  ///     transport in this container.</summary>
  /// <param name="vendorId">The vendor id from a selection entry.</param>
  /// <param name="config">The per-attempt model configuration.</param>
  Result<IModelProvider> Resolve(string vendorId, ModelConfig config);

  /// <summary>The catalog that serves <paramref name="vendorId"/>. Fails with a named
  ///     error when the vendor has no catalog in this container.</summary>
  /// <param name="vendorId">The vendor id from a selection entry.</param>
  Result<IModelCatalog> Catalog(string vendorId);
}
