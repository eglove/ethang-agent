using eThangAgent.ModelDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>The provider vocabulary: the composition root's <see cref="Providers"/> delegates to
///     <see cref="ModelVendors"/> rather than keeping a second copy of each literal, because the
///     Model Domain cannot reference this assembly and needs the same ids to carry a vendor on a
///     selection entry.</summary>
public class ProvidersVocabularyTests
{
  [Fact]
  public void TheTwoVocabularies_HoldTheSameLiterals()
  {
    Assert.Equal(Providers.OpenRouter, ModelVendors.OpenRouter);
    Assert.Equal(Providers.Cloudflare, ModelVendors.Cloudflare);
  }

  [Theory]
  [InlineData("openrouter")]
  [InlineData("cloudflare")]
  public void IsKnown_AnswersTrueForEveryKnownVendor(string id)
      => Assert.True(Providers.IsKnown(id));

  [Theory]
  [InlineData("gopher")]
  [InlineData("")]
  [InlineData(null)]
  public void IsKnown_AnswersFalseForAnythingElse(string? id)
      => Assert.False(Providers.IsKnown(id));

  [Theory]
  [InlineData("openrouter", "OpenRouter")]
  [InlineData("cloudflare", "Cloudflare")]
  [InlineData("gopher", "gopher")]
  public void DisplayName_NamesTheVendorAndFallsBackToTheRawId(string id, string expected)
      => Assert.Equal(expected, Providers.DisplayName(id));

  [Fact]
  public void FallbackModelId_StillAnswersForOpenRouter()
      => Assert.Equal(Providers.RoutingModelId, Providers.FallbackModelId(Providers.OpenRouter));
}
