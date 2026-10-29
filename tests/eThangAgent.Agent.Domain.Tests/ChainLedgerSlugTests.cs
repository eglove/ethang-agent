using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The chain ledger slug (issue #302 amendment). The slug is DERIVED rather than
///     stored, so these pin the derivation: a slug that drifted between the brief and the
///     system prompt would point two sessions at two different ledgers.</summary>
public sealed class ChainLedgerSlugTests
{
  [Theory]
  [InlineData("Dashboard truth", "dashboard-truth")]
  [InlineData("  Spaced  Out  ", "spaced-out")]
  [InlineData("Already-Slugged", "already-slugged")]
  [InlineData("MiXeD CaSe 42", "mixed-case-42")]
  [InlineData("punctuation!!!here", "punctuation-here")]
  public void For_ShapesTheTitleIntoOneKeySegment(string title, string expected) =>
      Assert.Equal(expected, ChainLedgerSlug.For(title), StringComparer.Ordinal);

  [Fact]
  public void For_DropsALeadingIsoDate() =>
      // The sdd.<slug> precedent drops the date so the namespace reads as the subject.
      Assert.Equal("dashboard-truth",
          ChainLedgerSlug.For("2026-08-24-dashboard-truth"), StringComparer.Ordinal);

  [Fact]
  public void For_KeepsANonDateLeadingNumber() =>
      Assert.Equal("42-things", ChainLedgerSlug.For("42 things"), StringComparer.Ordinal);

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("!!!")]
  public void For_WithNoUsableTitle_FallsBackRatherThanProducingAnEmptySegment(string? title) =>
      // An empty slug would produce the key 'sci./ledger', which is not a key.
      Assert.Equal(ChainLedgerSlug.Fallback, ChainLedgerSlug.For(title), StringComparer.Ordinal);

  [Fact]
  public void For_IsPure_TheSameTitleAlwaysYieldsTheSameSlug()
  {
    // The property that lets the brief (#302) and the system prompt (#311) agree.
    string first = ChainLedgerSlug.For("Dashboard truth");
    string second = ChainLedgerSlug.For("Dashboard truth");

    Assert.Equal(first, second, StringComparer.Ordinal);
  }

  [Fact]
  public void For_BoundsTheLengthSoTheKeyStaysRetypable()
  {
    string slug = ChainLedgerSlug.For(new string('a', 500));

    Assert.Equal(ChainLedgerSlug.MaxLength, slug.Length);
  }

  [Fact]
  public void For_ABoundedSlugNeverEndsWithAHyphen()
  {
    // A hyphen at the cut would read as a truncated word rather than as a bound.
    string title = new string('a', ChainLedgerSlug.MaxLength - 1) + " b";

    Assert.False(ChainLedgerSlug.For(title).EndsWith('-'));
  }

  [Fact]
  public void For_ProducesASingleKeySegmentWithNoSlashOrWhitespace()
  {
    // The experiment tool rejects a slug containing '/' or whitespace, so the derived slug
    // must satisfy the same rule or every ledger write would fail validation.
    string slug = ChainLedgerSlug.For("a/b c\td");

    Assert.DoesNotContain("/", slug, StringComparison.Ordinal);
    Assert.DoesNotContain(" ", slug, StringComparison.Ordinal);
    Assert.DoesNotContain("\t", slug, StringComparison.Ordinal);
  }
}
