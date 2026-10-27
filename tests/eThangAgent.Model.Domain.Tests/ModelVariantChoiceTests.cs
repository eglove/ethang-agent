using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The stored model choice: a model plus its routing VARIANT, and the codec a
///     workspace preference round-trips through.
///     <para>WHY IT REPLACES THE PROVIDER PAIR: the pair asked the user to name which of a
///     model's providers should serve, which the catalog serves as dozens of rows. The variant
///     records the one thing they can still say - default, fastest, or cheapest - and the wire
///     id is that intent appended to the model.</para></summary>
public class ModelVariantChoiceTests
{
  [Fact]
  public void ARoundTrip_PreservesBothHalves()
  {
    ModelVariantChoice original = new("deepseek/deepseek-v4.1-flash-1017", ModelVariant.Nitro);

    ModelVariantChoice parsed = ModelVariantChoice.Parse(original.Serialize()).ValueOrNull!;

    Assert.Equal("deepseek/deepseek-v4.1-flash-1017", parsed.ModelId);
    Assert.Equal(ModelVariant.Nitro, parsed.Variant);
  }

  [Fact]
  public void TheWireModelId_AppendsTheVariantToTheBaseModel()
  {
    Assert.Equal("alpha/model", new ModelVariantChoice("alpha/model").WireModelId);
    Assert.Equal("alpha/model:nitro", new ModelVariantChoice("alpha/model", ModelVariant.Nitro).WireModelId);
    Assert.Equal("alpha/model:floor", new ModelVariantChoice("alpha/model", ModelVariant.Floor).WireModelId);
  }

  [Fact]
  public void TheBaseModelId_IsWhatTheCatalogIsKeyedOn()
      => Assert.Equal("alpha/model",
          new ModelVariantChoice("alpha/model:nitro", ModelVariant.Nitro).BaseModelId);

  [Fact]
  public void ALegacyBareModelId_RestoresTheModelOnStandard()
  {
    // The oldest stored shape: a bare model id, no braces. The variant is unset rather than
    // guessed - the same rule the retired provider followed.
    ModelVariantChoice parsed = ModelVariantChoice.Parse("alpha/model").ValueOrNull!;

    Assert.Equal("alpha/model", parsed.ModelId);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
  }

  [Fact]
  public void ALegacyProviderPair_RestoresTheModelAndDropsTheProvider()
  {
    // The retired provider field is READ and IGNORED. Refusing the value would drop a model the
    // user did choose; restoring the provider is impossible, because the pin is what this
    // change deletes.
    ModelVariantChoice parsed = ModelVariantChoice.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","ProviderName":"DeepInfra"}""").ValueOrNull!;

    Assert.Equal("alpha/model", parsed.ModelId);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
  }

  [Fact]
  public void ALegacyModelIdCarryingAVariantSuffix_IsSplitIntoBothHalves()
  {
    // A value written before the variant field existed can still carry the suffix, because a
    // user could have typed it. Splitting keeps the stored value NORMALIZED (base id +
    // variant), so the picker preselects the right dropdown row instead of showing a model
    // whose id happens to end in ':nitro'.
    ModelVariantChoice parsed = ModelVariantChoice.Parse("alpha/model:nitro").ValueOrNull!;

    Assert.Equal("alpha/model", parsed.ModelId);
    Assert.Equal(ModelVariant.Nitro, parsed.Variant);
  }

  [Fact]
  public void AModelIdWhoseColonIsAPricingSuffix_IsNotSplit()
  {
    ModelVariantChoice parsed = ModelVariantChoice.Parse("vendor/model:free").ValueOrNull!;

    Assert.Equal("vendor/model:free", parsed.ModelId);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
  }

  [Fact]
  public void BlankStoredValue_IsANamedFailure()
  {
    Result<ModelVariantChoice> parsed = ModelVariantChoice.Parse("   ");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void MalformedJson_IsANamedFailure()
  {
    Result<ModelVariantChoice> parsed = ModelVariantChoice.Parse("{not json");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void AChoiceNamingNoModel_IsANamedFailure()
  {
    Result<ModelVariantChoice> parsed =
        ModelVariantChoice.Parse(/*lang=json,strict*/ """{"ModelId":""}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void AnUnknownVariant_IsANamedFailureRatherThanSilentlyServingStandard()
  {
    // STRICT AT THE BOUNDARY: a variant this build does not know may be one a later build
    // added, and serving Standard instead would route the turn the user did not ask for - the
    // silent substitution the harness refuses. The failure leaves the shell on its session
    // default, which the user can see and correct.
    Result<ModelVariantChoice> parsed = ModelVariantChoice.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","Variant":"Ultrafast"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void ANumericVariantText_IsNotAcceptedAsAValidMember()
  {
    // Enum.TryParse alone accepts "5" and yields an out-of-range value, which would smuggle a
    // member the build does not define into the domain. Parsing requires a DEFINED member.
    Result<ModelVariantChoice> parsed = ModelVariantChoice.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","Variant":"5"}""");

    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void AVariantName_IsAcceptedCaseInsensitively()
  {
    // The value is serialized from an enum name, and a hand-edited preference should not fail
    // on casing alone.
    ModelVariantChoice parsed = ModelVariantChoice.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","Variant":"floor"}""").ValueOrNull!;

    Assert.Equal(ModelVariant.Floor, parsed.Variant);
  }
}
