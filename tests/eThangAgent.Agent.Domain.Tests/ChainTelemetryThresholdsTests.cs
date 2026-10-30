using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #309 D5: the thresholds artifact parses STRICTLY, because a lenient parser
///     would let a typo silently restore a default nobody chose — which is exactly the
///     'thresholds chosen after the fact' failure the decision exists to prevent.</summary>
public class ChainTelemetryThresholdsTests
{
  /// <summary>A complete, valid artifact, in the format the implementing session wrote.
  ///     <para>BUILT FROM THE REQUIRED KEYS rather than pasted, so a member added to the record
  ///     without being added to <c>RequiredKeys</c> fails here rather than in production.</para>
  ///     </summary>
  private static string Valid(params string[] overrides)
  {
    Dictionary<string, string> values = ChainTelemetryThresholds.RequiredKeys
        .ToDictionary(key => key, _ => "0.5", StringComparer.Ordinal);
    foreach (string entry in overrides)
    {
      int equals = entry.IndexOf('=', StringComparison.Ordinal);
      values[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();
    }

    return string.Join("\n", values.Select(pair => pair.Key + " = " + pair.Value));
  }

  [Fact]
  public void Parse_OfACompleteArtifact_ReadsEveryMember()
  {
    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse(Valid(
        "intervention-share-max = 0.2",
        "median-completed-steps-min = 4",
        "review-deny-share-max = 0.6"));

    Assert.True(parsed.IsSuccess, parsed.Error?.Message ?? "expected success");
    Assert.Equal(0.2m, parsed.Value.InterventionShareMax);
    Assert.Equal(4m, parsed.Value.MedianCompletedStepsMin);
    Assert.Equal(0.6m, parsed.Value.ReviewDenyShareMax);
  }

  [Fact]
  public void Parse_AllowsCommentsAndBlankLines_BecauseTheArtifactExplainsItself()
  {
    // The artifact the implementing session wrote carries the REASON each threshold exists.
    // A parser demanding bare pairs would have thrown that reasoning away.
    string text = "# why this exists\n\n" + Valid() + "\n# trailing note\n";

    Assert.True(ChainTelemetryThresholds.Parse(text).IsSuccess);
  }

  [Fact]
  public void Parse_OfAnEmptyArtifact_IsRefused_NamingWhatToWrite()
  {
    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse("   \n\n");

    Assert.False(parsed.IsSuccess);
    Assert.Equal(ChainTelemetryThresholds.Code, parsed.Error.Code);
    Assert.Contains("before the measurement", parsed.Error.Message, StringComparison.Ordinal);
    Assert.Contains("intervention-share-max", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_WithAMissingKey_IsRefused_AndNamesOnlyWhatIsMissing()
  {
    string text = string.Join("\n", Valid().Split('\n')
        .Where(line => !line.StartsWith("park-share-max", StringComparison.Ordinal)));

    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse(text);

    Assert.False(parsed.IsSuccess);
    Assert.Contains("missing: park-share-max", parsed.Error.Message, StringComparison.Ordinal);
    // The keys that ARE present are not named, so a user who added one knows what is left.
    Assert.DoesNotContain("intervention-share-max", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_WithAnUnknownKey_IsRefused_RatherThanIgnored()
  {
    // An invented threshold is one the report would never read, so accepting it would let a
    // user believe a bound is in force when nothing checks it.
    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse(Valid() + "\nmy-own-metric = 1");

    Assert.False(parsed.IsSuccess);
    Assert.Contains("'my-own-metric' is not a telemetry threshold", parsed.Error.Message,
        StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_WithANonNumericValue_IsRefused_QuotingWhatItSaw()
  {
    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse(Valid("park-share-max = a quarter"));

    Assert.False(parsed.IsSuccess);
    Assert.Contains("must be a number, but reads 'a quarter'", parsed.Error.Message,
        StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_OfALineWithNoEquals_IsRefused()
  {
    Result<ChainTelemetryThresholds> parsed = ChainTelemetryThresholds.Parse(Valid() + "\nthis line is prose");

    Assert.False(parsed.IsSuccess);
    Assert.Contains("is not 'key = value'", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void KeyFor_DerivesFromTheLedgerSlug_SoThresholdsLiveBesideTheLedger()
  {
    // ONE derivation site: the key the implementing session wrote and the key the report reads
    // cannot disagree.
    Assert.Equal("sci.chain-telemetry/thresholds",
        ChainTelemetryThresholds.KeyFor("Chain telemetry"));
    // The slug lowercases and collapses, so two spellings of one title reach one key — the
    // property that makes the brief's key and the report's key the same key.
    Assert.Equal(ChainTelemetryThresholds.KeyFor("Chain telemetry"),
        ChainTelemetryThresholds.KeyFor("chain TELEMETRY"));
    // A leading ISO DATE is dropped by the slug (it is a sort key, not a name) — but only in
    // the `dddd-dd-dd-` shape the slug's own rule states, which is why this title keeps its
    // digits when the date is followed by a space.
    Assert.Equal("sci.2026-10-30-chain-telemetry/thresholds",
        ChainTelemetryThresholds.KeyFor("2026-10-30 Chain telemetry"));
    Assert.Equal("sci.chain-telemetry/thresholds",
        ChainTelemetryThresholds.KeyFor("2026-10-30-Chain telemetry"));
  }

  [Fact]
  public void KeyFor_OfAMissingTitle_FallsBackRatherThanProducingAnUnusableKey()
  {
    Assert.Equal("sci.chain/thresholds", ChainTelemetryThresholds.KeyFor(null));
    Assert.Equal("sci.chain/thresholds", ChainTelemetryThresholds.KeyFor("   "));
  }

  [Fact]
  public void RequiredKeys_CoversEveryMemberOfTheRecord()
  {
    // The guard that keeps the artifact and the record in step: a member added without a key
    // would be unparseable, and this fails rather than letting it reach a report as a zero.
    int members = typeof(ChainTelemetryThresholds).GetProperties()
        .Count(property => property.PropertyType == typeof(decimal));

    Assert.Equal(ChainTelemetryThresholds.RequiredKeys.Length, members);
    Assert.Equal(members, ChainTelemetryThresholds.RequiredKeys.Distinct(StringComparer.Ordinal).Count());
  }
}
