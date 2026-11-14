using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The model selection: an ORDERED list of models plus the one routing variant and the
///     one retention tier that cover the whole list, and the codec a workspace preference
///     round-trips through.
///     <para>ORDER IS THE DECISION. The head runs and the tail is the fallback the provider falls
///     through to, so a selection's identity includes the sequence of its ids, not only the set.
///     </para>
///     <para>THE SINGLE-CHOICE SHAPES ARE STILL READ: a bare id and the single-choice document
///     both restore as a one-entry list, because a workspace that saved a model before the list
///     existed must keep serving it.</para></summary>
public class ModelSelectionTests
{
  private static ModelSelection Selection(params string[] modelIds)
      => ModelSelection.Create(modelIds).ValueOrNull!;

  // ── the stored shape ─────────────────────────────────────────────────────

  [Fact]
  public void AOneEntrySelection_SerializesAsTheDocumentedShape_AndRoundTrips()
  {
    ModelSelection original = Selection("alpha/model");

    string serialized = original.Serialize();

    Assert.Equal(
        /*lang=json,strict*/
        """{"Models":["alpha/model"],"Variant":"Standard","Retention":"ZeroDataRetention"}""",
        serialized);
    Assert.Equal(original, ModelSelection.Parse(serialized).ValueOrNull);
  }

  [Fact]
  public void AThreeEntrySelection_PreservesOrderAcrossARoundTrip()
  {
    ModelSelection original = Selection("alpha/model", "beta/other", "gamma/third");

    ModelSelection parsed = ModelSelection.Parse(original.Serialize()).ValueOrNull!;

    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], parsed.ModelIds);
  }

  [Fact]
  public void TheSameIdsInADifferentOrder_AreNotTheSameSelection()
  {
    ModelSelection preferred = Selection("alpha/model", "beta/other");
    ModelSelection reversed = Selection("beta/other", "alpha/model");

    Assert.NotEqual(preferred, reversed);
    Assert.NotEqual(preferred.Head, reversed.Head);
  }

  [Fact]
  public void EveryTier_RoundTripsThroughSerializeAndParse()
  {
    foreach (RetentionPolicy tier in RetentionPolicies.All)
    {
      ModelSelection original = ModelSelection.Create(["alpha/model"], ModelVariant.Standard, tier).ValueOrNull!;

      ModelSelection parsed = ModelSelection.Parse(original.Serialize()).ValueOrNull!;

      Assert.Equal(tier, parsed.Retention);
    }
  }

  [Fact]
  public void Serialize_WritesTheTierAndNeverTheRetiredBoolean()
  {
    string serialized = ModelSelection.Create(["alpha/model"], ModelVariant.Standard,
        RetentionPolicy.SafetyRetention).ValueOrNull!.Serialize();

    Assert.Contains("\"Retention\":\"SafetyRetention\"", serialized, StringComparison.Ordinal);
    Assert.DoesNotContain("ZeroDataRetention\":", serialized, StringComparison.Ordinal);
  }

  // ── the wire form ────────────────────────────────────────────────────────

  [Fact]
  public void TheWireIds_ApplyTheVariantToEveryEntryInOrder()
  {
    ModelSelection nitro = ModelSelection.Create(["alpha/model", "beta/other"], ModelVariant.Nitro)
        .ValueOrNull!;

    Assert.Equal(["alpha/model:nitro", "beta/other:nitro"], nitro.WireModelIds);
  }

  [Fact]
  public void TheWireIdsOfAStandardSelection_CarryNoSuffix()
  {
    ModelSelection standard = Selection("alpha/model", "beta/other");

    Assert.Equal(["alpha/model", "beta/other"], standard.WireModelIds);
  }

  [Fact]
  public void ApplyingAVariantTwice_DoesNotDoubleTheSuffix_AndSwitchingReplacesIt()
  {
    // The variant is stored beside the ids, never inside them, so an id that already carries a
    // suffix must not end up with two, and switching variant must replace rather than stack.
    ModelSelection nitro = Selection("alpha/model:nitro") with { Variant = ModelVariant.Nitro };
    ModelSelection again = nitro with { Variant = ModelVariant.Nitro };
    ModelSelection floor = nitro with { Variant = ModelVariant.Floor };

    Assert.Equal(["alpha/model:nitro"], nitro.WireModelIds);
    Assert.Equal(["alpha/model:nitro"], again.WireModelIds);
    Assert.Equal(["alpha/model:floor"], floor.WireModelIds);
  }

  [Fact]
  public void ACacheFloorSelection_CarriesTheVariantAndLeavesTheIdsBare()
  {
    ModelSelection cacheFloor = ModelSelection.Create(["alpha/model", "beta/other"],
        ModelVariant.CacheFloor).ValueOrNull!;

    Assert.Equal(["alpha/model", "beta/other"], cacheFloor.WireModelIds);
    Assert.Equal(ModelVariant.CacheFloor, cacheFloor.Variant);
  }

  [Fact]
  public void TheHead_IsTheFirstEntry()
      => Assert.Equal("alpha/model", Selection("alpha/model", "beta/other").Head);

  // ── validating a live list ───────────────────────────────────────────────

  [Fact]
  public void AnEmptyList_IsANamedFailureTellingTheUserToAddAModel()
  {
    Result<ModelSelection> created = ModelSelection.Create([]);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidModelChoice", created.Error.Code);
    Assert.Contains("at least one model", created.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void AnAllWhitespaceList_IsANamedFailure()
  {
    Result<ModelSelection> created = ModelSelection.Create(["   ", "\t"]);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidModelChoice", created.Error.Code);
  }

  [Fact]
  public void ABlankEntry_IsANamedFailure_NamingThePosition()
  {
    Result<ModelSelection> created = ModelSelection.Create(["alpha/model", "  "]);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidModelChoice", created.Error.Code);
    Assert.Contains("position 2", created.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ADuplicateId_IsANamedFailure_NamingTheDuplicate()
  {
    Result<ModelSelection> created = ModelSelection.Create(["alpha/model", "beta/other", "alpha/model"]);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidModelChoice", created.Error.Code);
    Assert.Contains("alpha/model", created.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_TrimsSurroundingWhitespace_AndKeepsTheOrder()
  {
    ModelSelection created = ModelSelection.Create([" alpha/model ", "beta/other"], ModelVariant.Nitro,
        RetentionPolicy.SafetyRetention).ValueOrNull!;

    Assert.Equal(["alpha/model", "beta/other"], created.ModelIds);
    Assert.Equal(ModelVariant.Nitro, created.Variant);
    Assert.Equal(RetentionPolicy.SafetyRetention, created.Retention);
  }

  // ── the provider's three-model limit ─────────────────────────────────────

  [Fact]
  public void AThreeEntryList_IsAccepted_BecauseTheProviderTakesThree()
  {
    Result<ModelSelection> created = ModelSelection.Create(["alpha/model", "beta/other", "gamma/third"]);

    Assert.True(created.IsSuccess, created.Error?.Message);
    Assert.Equal(3, created.ValueOrNull!.ModelIds.Count);
  }

  [Fact]
  public void AFourthModel_IsANamedFailure_NamingTheCountAndTheLimit()
  {
    // The provider refuses a longer list with a 400 before any model serves, so a fourth entry
    // is a selection that can never run. The refusal names both numbers so the surplus entry is
    // obvious rather than something the user has to count.
    Result<ModelSelection> created = ModelSelection.Create(
        ["alpha/model", "beta/other", "gamma/third", "delta/fourth"]);

    Assert.False(created.IsSuccess);
    Assert.Equal("InvalidModelChoice", created.Error.Code);
    Assert.Contains("4 models", created.Error.Message, StringComparison.Ordinal);
    Assert.Contains("at most 3", created.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AStoredListLongerThanTheLimit_RestoresItsFirstThreeEntries()
  {
    // A value written before the limit existed, or hand-edited past it. The surplus can never
    // serve, and refusing the whole value would drop the head the user did choose.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["alpha/model","beta/other","gamma/third","delta/fourth"],"Variant":"Standard"}""")
        .ValueOrNull!;

    Assert.Equal(["alpha/model", "beta/other", "gamma/third"], parsed.ModelIds);
    Assert.Equal("alpha/model", parsed.Head);
  }

  // ── the legacy shapes ────────────────────────────────────────────────────

  [Fact]
  public void ALegacyBareModelId_RestoresAsAOneEntryListOnStandard()
  {
    // The oldest stored shape: a bare model id, no braces. The variant is unset rather than
    // guessed - the same rule the retired provider followed.
    ModelSelection parsed = ModelSelection.Parse("alpha/model").ValueOrNull!;

    Assert.Equal(["alpha/model"], parsed.ModelIds);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
    Assert.Equal(RetentionPolicy.ZeroDataRetention, parsed.Retention);
  }

  [Fact]
  public void ALegacyModelIdCarryingAVariantSuffix_IsSplitIntoBaseIdAndVariant()
  {
    // Splitting keeps the stored value NORMALIZED (base id + variant), so the picker preselects
    // the right dropdown row instead of showing a model whose id happens to end in ':nitro'.
    ModelSelection parsed = ModelSelection.Parse("alpha/model:nitro").ValueOrNull!;

    Assert.Equal(["alpha/model"], parsed.ModelIds);
    Assert.Equal(ModelVariant.Nitro, parsed.Variant);
  }

  [Fact]
  public void ALegacySingleChoiceDocument_RestoresAsAOneEntryList()
  {
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"ModelId":"alpha/model","Variant":"Nitro","Retention":"SafetyRetention"}""").ValueOrNull!;

    Assert.Equal(["alpha/model"], parsed.ModelIds);
    Assert.Equal(ModelVariant.Nitro, parsed.Variant);
    Assert.Equal(RetentionPolicy.SafetyRetention, parsed.Retention);
  }

  [Fact]
  public void ALegacyProviderPair_RestoresTheModelAndDropsTheProvider()
  {
    // The retired provider field is READ and IGNORED. Refusing the value would drop a model the
    // user did choose; restoring the provider is impossible, because the pin is what that
    // change deleted.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","ProviderName":"DeepInfra"}""").ValueOrNull!;

    Assert.Equal(["alpha/model"], parsed.ModelIds);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
  }

  [Fact]
  public void AModelIdWhoseColonIsAPricingSuffix_IsNotSplit()
  {
    ModelSelection parsed = ModelSelection.Parse("vendor/model:free").ValueOrNull!;

    Assert.Equal(["vendor/model:free"], parsed.ModelIds);
    Assert.Equal(ModelVariant.Standard, parsed.Variant);
  }

  [Fact]
  public void ALegacyDocumentWithoutTheTierMember_RestoresTheStrictTier()
  {
    // A value written before the tiers existed must read back strict: the absence of a decision
    // is not a decision to loosen.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","Variant":"Nitro"}""").ValueOrNull!;

    Assert.Equal(RetentionPolicy.ZeroDataRetention, parsed.Retention);
  }

  [Fact]
  public void ALegacyFalseBoolean_ReadsBackAsNoRestriction()
  {
    // The boolean could only express two tiers: false meant 'no restriction'.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"ModelId":"alpha/model","Variant":"Standard","ZeroDataRetention":false}""").ValueOrNull!;

    Assert.Equal(RetentionPolicy.RetentionAndTraining, parsed.Retention);
  }

  [Fact]
  public void ALegacyTrueBoolean_ReadsBackAsTheStrictTier()
  {
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"ModelId":"alpha/model","Variant":"Standard","ZeroDataRetention":true}""").ValueOrNull!;

    Assert.Equal(RetentionPolicy.ZeroDataRetention, parsed.Retention);
  }

  [Fact]
  public void ALegacyFalseBoolean_WithABlankVariant_AlsoRestoresItsTier()
  {
    // The retired provider-pair payload has no variant member, so the tier must be resolved on
    // that branch too rather than defaulting.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"ModelId":"alpha/model","ZeroDataRetention":false}""").ValueOrNull!;

    Assert.Equal(RetentionPolicy.RetentionAndTraining, parsed.Retention);
  }

  // ── failures ─────────────────────────────────────────────────────────────

  [Fact]
  public void BlankStoredValue_IsANamedFailure()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse("   ");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void MalformedJson_IsANamedFailureCarryingTheParseMessage()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse("{not json");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
    Assert.Contains("not valid JSON", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AStoredList_IsNormalizedToBaseIds_SoTheCatalogKeyIsTheStoredId()
  {
    // The variant is stored once beside the list, never on each entry, so a stored id that
    // carries a suffix is normalized back to the base id the catalog is keyed on.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":["alpha/model:nitro","beta/other"],"Variant":"Nitro"}""")
        .ValueOrNull!;

    Assert.Equal(["alpha/model", "beta/other"], parsed.ModelIds);
    Assert.Equal(["alpha/model:nitro", "beta/other:nitro"], parsed.WireModelIds);
  }

  [Fact]
  public void AStoredListWithABlankEntry_IsANamedFailure_NotASilentlyShortenedList()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":["alpha/model","  "],"Variant":"Standard"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void ADocumentNamingNoModel_IsANamedFailure()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse(/*lang=json,strict*/ """{"ModelId":""}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void AnEmptyModelsArray_IsANamedFailureNamingTheAtLeastOneModelRule()
  {
    // An empty list has no head: there is nothing to serve and no id to report, so it is a
    // refusal rather than a selection the rest of the harness has to guard against.
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":[],"Variant":"Standard"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
    Assert.Contains("at least one model", parsed.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void AnUnknownVariant_IsANamedFailureRatherThanSilentlyServingStandard()
  {
    // STRICT AT THE BOUNDARY: a variant this build does not know may be one a later build
    // added, and serving Standard instead would route the turn the user did not ask for - the
    // silent substitution the harness refuses.
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":["alpha/model"],"Variant":"Ultrafast"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void ANumericVariantText_IsNotAcceptedAsAValidMember()
  {
    // Enum.TryParse alone accepts "5" and yields an out-of-range value, which would smuggle a
    // member the build does not define into the domain. Parsing requires a DEFINED member.
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":["alpha/model"],"Variant":"5"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void AVariantName_IsAcceptedCaseInsensitively()
  {
    // The value is serialized from an enum name, and a hand-edited preference should not fail
    // on casing alone.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"Models":["alpha/model"],"Variant":"nitro"}""").ValueOrNull!;

    Assert.Equal(ModelVariant.Nitro, parsed.Variant);
  }

  [Fact]
  public void AnUnknownTier_IsANamedFailure_NotASilentStrictTier()
  {
    // It may name a tier a later build added; substituting the strict tier would tighten a
    // session the user had loosened.
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["alpha/model"],"Variant":"Standard","Retention":"Paranoid"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }

  [Fact]
  public void AnOutOfRangeTierNumber_IsANamedFailure()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["alpha/model"],"Variant":"Standard","Retention":"5"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidModelChoice", parsed.Error.Code);
  }
}
