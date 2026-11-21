using eThangAgent.SharedKernel;

namespace eThangAgent.ModelDomain.Tests;

/// <summary>The vendor-carrying selection: an entry pairs a model id with the provider that
///     serves it, and the selection round-trips that pair through the stored document.
///     <para>THE SAME MODEL UNDER TWO VENDORS IS TWO LEGAL ENTRIES, because they differ in price
///     and in quota; a repeat is the same model under the SAME vendor.</para>
///     <para>A SELECTION THAT NAMES NO VENDOR WRITES THE DOCUMENT EARLIER BUILDS WROTE, so a
///     preference saved by this build is still readable by its predecessor.</para></summary>
public class ModelSelectionVendorTests
{
  private const string InvalidChoice = "InvalidModelChoice";

  private static ModelSelectionEntry Entry(string modelId, string? vendor = null)
      => new(modelId, vendor);

  // ── the entry pair ───────────────────────────────────────────────────────

  [Fact]
  public void Create_FromBareIds_LeavesEveryVendorUnset()
  {
    ModelSelection created = ModelSelection.Create(["deepseek/deepseek-chat"]).ValueOrNull!;

    Assert.Equal([Entry("deepseek/deepseek-chat")], created.Entries);
    Assert.Null(created.Entries[0].ProviderId);
    Assert.False(created.Entries[0].HasVendor);
  }

  [Fact]
  public void Create_FromEntries_CarriesTheVendorOnTheEntryThatNamesOne()
  {
    ModelSelection created = ModelSelection.Create(
        [Entry("a/b"), Entry("@cf/meta/llama-3.1-8b-instruct", "cloudflare")]).ValueOrNull!;

    Assert.Equal(2, created.Entries.Count);
    Assert.Null(created.Entries[0].ProviderId);
    Assert.Equal("cloudflare", created.Entries[1].ProviderId);
    Assert.Equal(["a/b", "@cf/meta/llama-3.1-8b-instruct"], created.ModelIds);
  }

  [Fact]
  public void ABlankVendorId_IsUnset_NotAVendorNamedAsEmptyString()
  {
    ModelSelection created = ModelSelection.Create([Entry("a/b", "   ")]).ValueOrNull!;

    Assert.Null(created.Entries[0].Vendor);
    Assert.False(created.Entries[0].HasVendor);
  }

  [Fact]
  public void AnUnknownVendor_IsRefusedNamingItsPositionAndTheId()
  {
    Result<ModelSelection> created = ModelSelection.Create([Entry("a/b", "gopher")]);

    Assert.False(created.IsSuccess);
    Assert.Equal(InvalidChoice, created.Error.Code);
    Assert.Contains("1", created.Error.Message, StringComparison.Ordinal);
    Assert.Contains("gopher", created.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void TheSameModelUnderTwoVendors_IsAccepted()
  {
    // The spec makes the same model served by both vendors two rows: they differ in price and
    // in quota, so the list must be able to hold both.
    Result<ModelSelection> created =
        ModelSelection.Create([Entry("a/b"), Entry("a/b", "cloudflare")]);

    Assert.True(created.IsSuccess, created.Error?.Message);
    Assert.Equal(2, created.Value.Entries.Count);
  }

  [Fact]
  public void TheSameModelTwiceUnderTheSameVendor_IsRefused()
  {
    Result<ModelSelection> created = ModelSelection.Create([Entry("a/b"), Entry("a/b")]);

    Assert.False(created.IsSuccess);
    Assert.Equal(InvalidChoice, created.Error.Code);
    Assert.Contains("a/b", created.Error.Message, StringComparison.Ordinal);
  }

  // ── equality ─────────────────────────────────────────────────────────────

  [Fact]
  public void TwoSelectionsWithTheSameIdsUnderDifferentVendors_AreDifferent()
  {
    ModelSelection plain = ModelSelection.Create([Entry("a/b")]).ValueOrNull!;
    ModelSelection cloudflare = ModelSelection.Create([Entry("a/b", "cloudflare")]).ValueOrNull!;

    Assert.NotEqual(plain, cloudflare);
  }

  [Fact]
  public void TwoSelectionsWithTheSameEntriesInDifferentOrders_AreDifferent()
  {
    ModelSelection first = ModelSelection.Create([Entry("a/b"), Entry("c/d")]).ValueOrNull!;
    ModelSelection second = ModelSelection.Create([Entry("c/d"), Entry("a/b")]).ValueOrNull!;

    Assert.NotEqual(first, second);
  }

  // ── the stored shapes ────────────────────────────────────────────────────

  [Fact]
  public void Serialize_WithNoVendorNamed_WritesTheStringArrayShape()
  {
    string serialized = ModelSelection.Create(["a/b"]).ValueOrNull!.Serialize();

    Assert.DoesNotContain("Provider", serialized, StringComparison.Ordinal);
    Assert.Contains("\"Models\":[\"a/b\"]", serialized, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_WithAVendorNamed_WritesTheObjectArrayShape()
  {
    string serialized =
        ModelSelection.Create([Entry("a/b"), Entry("c/d", "cloudflare")]).ValueOrNull!.Serialize();

    Assert.Contains("\"Provider\":\"cloudflare\"", serialized, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_RestoresTheVendorFromTheObjectArrayShape()
  {
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":[{"ModelId":"a/b","Provider":"cloudflare"}],"Variant":"Standard","Retention":"ZeroDataRetention"}""")
        .ValueOrNull!;

    Assert.Equal("cloudflare", parsed.Entries[0].ProviderId);
  }

  [Fact]
  public void Parse_RestoresAnAbsentOrBlankProviderAsUnset()
  {
    ModelSelection absent = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":[{"ModelId":"a/b"}],"Variant":"Standard"}""").ValueOrNull!;
    ModelSelection blank = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":[{"ModelId":"a/b","Provider":"  "}],"Variant":"Standard"}""").ValueOrNull!;

    Assert.Null(absent.Entries[0].ProviderId);
    Assert.Null(blank.Entries[0].ProviderId);
  }

  [Fact]
  public void Parse_RefusesAMixedArray()
  {
    // This harness never wrote an array holding both a bare string and an object, so the value
    // is unknown input rather than a shape to guess at.
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["a/b",{"ModelId":"c/d","Provider":"openrouter"}],"Variant":"Standard"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal(InvalidChoice, parsed.Error.Code);
  }

  [Fact]
  public void Parse_RefusesAnUnknownVendorNamingItsPositionAndTheId()
  {
    Result<ModelSelection> parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":[{"ModelId":"a/b","Provider":"gopher"}],"Variant":"Standard"}""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal(InvalidChoice, parsed.Error.Code);
    Assert.Contains("1", parsed.Error.Message, StringComparison.Ordinal);
    Assert.Contains("gopher", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RoundTrip_KeepsEveryEntrysVendorInOrder()
  {
    ModelSelection original = ModelSelection.Create(
        [Entry("a/b"), Entry("c/d", "cloudflare"), Entry("e/f", "openrouter")],
        ModelVariant.Nitro, RetentionPolicy.SafetyRetention).ValueOrNull!;

    ModelSelection parsed = ModelSelection.Parse(original.Serialize()).ValueOrNull!;

    Assert.Equal(original, parsed);
    Assert.Equal("cloudflare", parsed.Entries[1].ProviderId);
    Assert.Equal("openrouter", parsed.Entries[2].ProviderId);
  }

  [Fact]
  public void Parse_ALegacyBareId_RestoresWithNoVendor()
  {
    ModelSelection parsed = ModelSelection.Parse("a/b").ValueOrNull!;

    Assert.Equal([Entry("a/b")], parsed.Entries);
  }

  [Fact]
  public void Parse_ALegacySingleChoiceDocument_RestoresWithNoVendor()
  {
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/ """{"ModelId":"a/b","Variant":"Standard"}""").ValueOrNull!;

    Assert.Equal([Entry("a/b")], parsed.Entries);
  }

  [Fact]
  public void Parse_ALegacyStringArrayDocument_RestoresWithNoVendor()
  {
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["a/b"],"Variant":"Standard","Retention":"ZeroDataRetention"}""").ValueOrNull!;

    Assert.Equal([Entry("a/b")], parsed.Entries);
  }

  [Fact]
  public void Parse_AStoredListLongerThanTheCap_StillRestoresItsFirstThree()
  {
    // The cap is removed in a later step; this step keeps it, and this pins that it does.
    ModelSelection parsed = ModelSelection.Parse(
        /*lang=json,strict*/
        """{"Models":["a/b","c/d","e/f","g/h"],"Variant":"Standard"}""").ValueOrNull!;

    Assert.Equal(["a/b", "c/d", "e/f"], parsed.ModelIds);
  }

  [Fact]
  public void Create_StillRefusesAListLongerThanTheCap()
  {
    Result<ModelSelection> created =
        ModelSelection.Create([Entry("a/b"), Entry("c/d"), Entry("e/f"), Entry("g/h")]);

    Assert.False(created.IsSuccess);
    Assert.Equal(InvalidChoice, created.Error.Code);
  }
}
