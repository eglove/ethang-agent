namespace eThangAgent.ModelDomain.Tests;

/// <summary>The three routing variants OpenRouter offers as MODEL-ID SUFFIXES (Standard,
///     Nitro, Floor): the one model-selection decision the picker still asks, expressed as the
///     wire id the provider itself routes on.
///     <para>WHY THE ID AND NOT A ROUTING OBJECT: OpenRouter documents <c>:nitro</c> and
///     <c>:floor</c> as suffixes on the model id - "Append :nitro to any model ID" - and the
///     suffix is what sorts the endpoint pool by throughput (admitting priority-tier
///     endpoints) or by price (admitting flex-tier endpoints). Standard appends nothing, which
///     is exactly the provider's own routing and therefore the pre-existing behaviour.
///     </para></summary>
public class ModelVariantTests
{
  [Fact]
  public void Standard_LeavesTheModelIdAlone()
      => Assert.Equal("alpha/model", ModelVariants.ApplyTo(ModelVariant.Standard, "alpha/model"));

  [Fact]
  public void Nitro_AppendsTheDocumentedSuffix()
      => Assert.Equal("alpha/model:nitro", ModelVariants.ApplyTo(ModelVariant.Nitro, "alpha/model"));

  [Fact]
  public void Floor_AppendsTheDocumentedSuffix()
      => Assert.Equal("alpha/model:floor", ModelVariants.ApplyTo(ModelVariant.Floor, "alpha/model"));

  [Fact]
  public void ApplyTo_IsIdempotent_SoAReappliedVariantCannotDoubleTheSuffix()
      => Assert.Equal("alpha/model:nitro", ModelVariants.ApplyTo(ModelVariant.Nitro, "alpha/model:nitro"));

  [Fact]
  public void SwitchingVariant_ReplacesThePreviousSuffixRatherThanStacking()
      => Assert.Equal("alpha/model:floor", ModelVariants.ApplyTo(ModelVariant.Floor, "alpha/model:nitro"));

  [Fact]
  public void BaseOf_StripsTheVariant_SoTheCatalogIsKeyedOnTheRealModel()
  {
    Assert.Equal("alpha/model", ModelVariants.BaseOf("alpha/model:nitro"));
    Assert.Equal("alpha/model", ModelVariants.BaseOf("alpha/model:floor"));
    Assert.Equal("alpha/model", ModelVariants.BaseOf("alpha/model"));
  }

  [Fact]
  public void FromModelId_ReadsTheVariantBackOut()
  {
    Assert.Equal(ModelVariant.Nitro, ModelVariants.FromModelId("alpha/model:nitro"));
    Assert.Equal(ModelVariant.Floor, ModelVariants.FromModelId("alpha/model:floor"));
    Assert.Equal(ModelVariant.Standard, ModelVariants.FromModelId("alpha/model"));
  }

  [Fact]
  public void AColonThatIsNotAVariant_IsPartOfTheModelId_NotAVariantSuffix()
  {
    // Real catalog ids carry colons of their own - pricing suffixes like ':free' are not
    // routing variants. Only the two documented routing suffixes may be consumed, and only as
    // the trailing segment.
    Assert.Equal("vendor/model:free", ModelVariants.BaseOf("vendor/model:free"));
    Assert.Equal(ModelVariant.Standard, ModelVariants.FromModelId("vendor/model:free"));
  }

  [Fact]
  public void EveryVariant_HasADistinctDisplayLabel()
  {
    string[] labels = [.. ModelVariants.All.Select(ModelVariants.Describe)];
    Assert.Equal(ModelVariants.All.Count, labels.Length);
    Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
    Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public void Describe_IsTheBareName_TheCaptionCarriesTheTradeoff()
  {
    // The label is the variant's NAME, not a sentence: the control that offers the three choices
    // states the tradeoff in the caption underneath it, so repeating it in every row and in the
    // status bar only lengthened the decision. Pinned exactly so a longer label cannot creep back.
    Assert.Equal("Standard", ModelVariants.Describe(ModelVariant.Standard));
    Assert.Equal("Nitro", ModelVariants.Describe(ModelVariant.Nitro));
    Assert.Equal("Floor", ModelVariants.Describe(ModelVariant.Floor));
  }

  [Fact]
  public void TheVariantList_CoversEveryEnumMember_SoNoVariantIsUnreachableFromThePicker()
  {
    Assert.Equal(Enum.GetValues<ModelVariant>().Length, ModelVariants.All.Count);
    Assert.Equal(ModelVariant.Standard, ModelVariants.All[0]);
  }
}
