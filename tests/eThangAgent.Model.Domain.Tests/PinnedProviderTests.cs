using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The pinned-provider rule (issue #297, D5): a provider the catalog no longer offers
///     for the chosen model fails the turn by name, and the two shapes that must NOT be read
///     as a vanished provider - an unreadable catalog and an empty one - stay silent.</summary>
public class PinnedProviderTests
{
  private static ModelProviderEntry Entry(string modelId, string? providerName) => new(
      modelId, providerName, 0.000001m, null, 0.000002m, 128_000, 8_192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      null, null, null, null, null, null);

  private static readonly IReadOnlyList<ModelProviderEntry> Catalog =
  [
      Entry("alpha/model", "DeepInfra"),
      Entry("alpha/model", "Fireworks"),
  ];

  [Fact]
  public void AServablePin_IsNotAFailure()
      => Assert.Null(PinnedProvider.MissingFor(Catalog, "alpha/model", "DeepInfra"));

  [Fact]
  public void AnUnsetPin_IsNotAFailure()
      => Assert.Null(PinnedProvider.MissingFor(Catalog, "alpha/model", null));

  [Fact]
  public void AProviderTheCatalogNoLongerOffers_IsANamedFailureNamingBoth()
  {
    DomainError? error = PinnedProvider.MissingFor(Catalog, "alpha/model", "Reka");

    Assert.NotNull(error);
    Assert.Equal("UnknownPinnedProvider", error.Code);
    Assert.Contains("Reka", error.Message, StringComparison.Ordinal);
    Assert.Contains("alpha/model", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AnUnreadableCatalog_IsNotAFailure()
      => Assert.Null(PinnedProvider.MissingFor(null, "alpha/model", "DeepInfra"));

  [Fact]
  public void AnEmptyCatalog_IsNotAFailure()
      => Assert.Null(PinnedProvider.MissingFor([], "alpha/model", "DeepInfra"));

  [Fact]
  public void AnUnknownModel_IsNotThisChecksFailure()
      => Assert.Null(PinnedProvider.MissingFor(Catalog, "beta/unlisted", "DeepInfra"));
}
