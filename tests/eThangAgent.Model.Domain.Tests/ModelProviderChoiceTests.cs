using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The persisted model + provider pair (issue #296, D7): the pair round-trips, a
///     provider name may contain any character, and the retired bare-model-id value restores
///     the model with the provider UNSET rather than guessed.</summary>
public class ModelProviderChoiceTests
{
  [Fact]
  public void ThePair_RoundTrips()
  {
    ModelProviderChoice original = new("deepseek/deepseek-v4.1-flash-1017", "DeepInfra");

    ModelProviderChoice parsed = ModelProviderChoice.Parse(original.Serialize()).Value!;

    Assert.Equal(original, parsed);
  }

  [Fact]
  public void APairWithoutAProvider_RoundTrips_WithTheProviderUnset()
  {
    ModelProviderChoice parsed = ModelProviderChoice.Parse(
        new ModelProviderChoice("alpha/model").Serialize()).Value!;

    Assert.Equal("alpha/model", parsed.ModelId);
    Assert.Null(parsed.ProviderName);
  }

  [Fact]
  public void AProviderNameContainingADelimiter_Survives()
  {
    // JSON rather than a delimited string, precisely so a name the catalog chooses cannot
    // split the pair into a model that does not exist and a provider nobody chose.
    ModelProviderChoice original = new("alpha/model", "Some:Provider, Inc. | v2 {x}");

    ModelProviderChoice parsed = ModelProviderChoice.Parse(original.Serialize()).Value!;

    Assert.Equal(original, parsed);
  }

  [Fact]
  public void TheLegacyBareModelId_RestoresTheModel_AndLeavesTheProviderUnset()
  {
    // D7's legacy rule: the retired key holds a bare model id. The model comes back; the
    // provider stays unset, because a provider the user never chose is never invented.
    ModelProviderChoice parsed = ModelProviderChoice.Parse("anthropic/claude").Value!;

    Assert.Equal("anthropic/claude", parsed.ModelId);
    Assert.Null(parsed.ProviderName);
  }

  [Fact]
  public void AnEmptyStoredValue_IsANamedFailure()
  {
    AssertFailure(ModelProviderChoice.Parse("   "));
    AssertFailure(ModelProviderChoice.Parse(null));
  }

  [Fact]
  public void CorruptJson_IsANamedFailure_NotAnException()
      => AssertFailure(ModelProviderChoice.Parse("{ not json"));

  [Fact]
  public void AJsonPairNamingNoModel_IsANamedFailure()
      => AssertFailure(ModelProviderChoice.Parse(/*lang=json,strict*/ """{"ModelId":"","ProviderName":"X"}"""));

  /// <summary>Every unreadable stored value is the SAME named failure: a caller branches on
  ///     the code, never on the message.</summary>
  private static void AssertFailure(Result<ModelProviderChoice> result)
  {
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidModelChoice", result.Error?.Code);
  }

  [Fact]
  public void ABlankProviderNameInJson_ReadsAsUnset()
  {
    ModelProviderChoice parsed = ModelProviderChoice.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","ProviderName":"  "}""").Value!;

    Assert.Null(parsed.ProviderName);
  }
}
