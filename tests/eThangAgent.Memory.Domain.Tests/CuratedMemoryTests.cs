using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

public class CuratedMemoryTests
{
  // ---- CuratedMemorySpecifications.ValidTag ----

  [Theory]
  [InlineData("a")]
  [InlineData("0")]
  public void ValidTag_SingleCharacter_IsAccepted(string tag) => Assert.True(CuratedMemorySpecifications.ValidTag(tag));

  [Theory]
  [InlineData("-abc")]
  [InlineData("-")]
  public void ValidTag_LeadingDash_IsRejected(string tag) => Assert.False(CuratedMemorySpecifications.ValidTag(tag));

  [Theory]
  [InlineData("_abc")]
  [InlineData("_")]
  public void ValidTag_LeadingUnderscore_IsRejected(string tag) => Assert.False(CuratedMemorySpecifications.ValidTag(tag));

  [Fact]
  public void ValidTag_Exactly32Chars_IsAccepted()
  {
    Assert.True(CuratedMemorySpecifications.ValidTag(new string('a', 32)));
    Assert.True(CuratedMemorySpecifications.ValidTag("a" + new string('-', 31)));
    Assert.True(CuratedMemorySpecifications.ValidTag("z" + new string('_', 31)));
  }

  [Fact]
  public void ValidTag_33Chars_IsRejected() => Assert.False(CuratedMemorySpecifications.ValidTag(new string('a', 33)));

  [Theory]
  [InlineData("")]
  [InlineData("Abc")]
  [InlineData("a b")]
  [InlineData("café")]
  public void ValidTag_InvalidCharset_IsRejected(string tag) => Assert.False(CuratedMemorySpecifications.ValidTag(tag));

  // ---- CuratedMemorySpecifications.NormalizeTags ----

  [Fact]
  public void NormalizeTags_DeduplicatesByOrdinalComparison()
  {
    IReadOnlyList<string> normalized = CuratedMemorySpecifications.NormalizeTags(
        ["beta", "alpha", "beta", "alpha"]);

    Assert.Equal(["beta", "alpha"], normalized);
  }

  [Fact]
  public void NormalizeTags_DedupIsExactOrdinal_NotSimilarityBased()
  {
    IReadOnlyList<string> normalized = CuratedMemorySpecifications.NormalizeTags(
        ["tag-1", "tag_1", "tag-1", "tag-11"]);

    Assert.Equal(["tag-1", "tag_1", "tag-11"], normalized);
  }

  [Fact]
  public void NormalizeTags_PreservesFirstSeenOrder()
  {
    IReadOnlyList<string> normalized = CuratedMemorySpecifications.NormalizeTags(
        ["c", "a", "b", "a", "c"]);

    Assert.Equal(["c", "a", "b"], normalized);
  }

  [Fact]
  public void NormalizeTags_EmptyInput_YieldsEmptyList()
  {
    IReadOnlyList<string> normalized = CuratedMemorySpecifications.NormalizeTags([]);

    Assert.Empty(normalized);
  }

  [Theory]
  [InlineData("Bad Tag")]
  [InlineData("UPPER")]
  [InlineData("with.dot")]
  public void NormalizeTags_InvalidCharset_ThrowsArgumentException(string tag)
  {
    ArgumentException ex = Assert.Throws<ArgumentException>(
        () => CuratedMemorySpecifications.NormalizeTags(["ok", tag]));

    Assert.Contains(tag, ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NormalizeTags_TooLongTag_ThrowsArgumentException()
  {
    _ = Assert.Throws<ArgumentException>(
        () => CuratedMemorySpecifications.NormalizeTags([new string('a', 33)]));
  }

  [Fact]
  public void NormalizeTags_NullEntry_ThrowsArgumentException()
  {
    _ = Assert.Throws<ArgumentException>(
        () => CuratedMemorySpecifications.NormalizeTags(["ok", null!]));
  }

  // ---- Enum wire parsing ----

  [Theory]
  [InlineData("convention", MemoryCategory.Convention)]
  [InlineData("preference", MemoryCategory.Preference)]
  [InlineData("insight", MemoryCategory.Insight)]
  [InlineData("failure", MemoryCategory.Failure)]
  [InlineData("reference", MemoryCategory.Reference)]
  public void ParseCategory_ExactLowercase_RoundTrips(string raw, MemoryCategory expected)
  {
    Result<MemoryCategory> result = CuratedMemorySpecifications.ParseCategory(raw);

    Assert.True(result.IsSuccess);
    Assert.Equal(expected, result.Value);
  }

  [Theory]
  [InlineData("Convention")]
  [InlineData("CONVENTION")]
  [InlineData("unknown")]
  [InlineData("")]
  public void ParseCategory_OtherThanExactLowercase_FailsWithTypedErrorListingAllowedValues(string raw)
  {
    Result<MemoryCategory> result = CuratedMemorySpecifications.ParseCategory(raw);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidCategory", result.Error!.Code);
    Assert.Contains("convention", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("preference", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("insight", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("failure", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("reference", result.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("workspace", MemoryScope.Workspace)]
  [InlineData("global", MemoryScope.Global)]
  public void ParseScope_ExactLowercase_RoundTrips(string raw, MemoryScope expected)
  {
    Result<MemoryScope> result = CuratedMemorySpecifications.ParseScope(raw);

    Assert.True(result.IsSuccess);
    Assert.Equal(expected, result.Value);
  }

  [Theory]
  [InlineData("Workspace")]
  [InlineData("GLOBAL")]
  [InlineData("session")]
  [InlineData("")]
  public void ParseScope_OtherThanExactLowercase_FailsWithTypedErrorListingAllowedValues(string raw)
  {
    Result<MemoryScope> result = CuratedMemorySpecifications.ParseScope(raw);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidScope", result.Error!.Code);
    Assert.Contains("workspace", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains("global", result.Error.Message, StringComparison.Ordinal);
  }

  // ---- Content budget ----

  [Fact]
  public void MaxContentChars_Is4000() => Assert.Equal(4000, CuratedMemorySpecifications.MaxContentChars);

  // ---- Aggregate shape ----

  [Fact]
  public void WithExpression_YieldsNewInstanceWithCopiedValues()
  {
    CuratedMemory original = new(
        Guid.NewGuid(),
        "ws-1",
        MemoryCategory.Convention,
        ["powershell"],
        "content",
        "hint",
        MemoryScope.Workspace,
        "session",
        Version: 3,
        CreatedAt: new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 8, 21, 13, 0, 0, TimeSpan.Zero));

    CuratedMemory copy = original with { };

    Assert.NotSame(original, copy);
    Assert.Equal(original.Id, copy.Id);
    Assert.Equal(original.WorkspaceId, copy.WorkspaceId);
    Assert.Equal(original.Category, copy.Category);
    Assert.Equal(original.Content, copy.Content);
    Assert.Equal(original.UsageHint, copy.UsageHint);
    Assert.Equal(original.Scope, copy.Scope);
    Assert.Equal(original.ProvenanceSession, copy.ProvenanceSession);
    Assert.Equal(original.Version, copy.Version);
    Assert.Equal(original.CreatedAt, copy.CreatedAt);
    Assert.Equal(original.UpdatedAt, copy.UpdatedAt);

    CuratedMemory mutated = original with { Version = 4 };
    Assert.Equal(3, original.Version);
    Assert.Equal(4, mutated.Version);
  }
}
