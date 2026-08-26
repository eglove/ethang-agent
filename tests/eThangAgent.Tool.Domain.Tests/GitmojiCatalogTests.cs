using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class GitmojiCatalogTests
{
  [Fact]
  public void All_LoadsExactly66Entries() =>
      Assert.Equal(66, GitmojiCatalog.All.Count);

  [Fact]
  public void Lookup_KnownKey_ReturnsExactRecord()
  {
    Result<Gitmoji> result = GitmojiCatalog.Lookup(":tada:");
    Assert.True(result.IsSuccess);
    Assert.Equal(new Gitmoji(":tada:", "🎉", "Initial commit"), result.Value);
  }

  [Fact]
  public void Lookup_BareName_Rejected()
  {
    Result<Gitmoji> result = GitmojiCatalog.Lookup("tada");
    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownEmojiKey", result.Error!.Code);
    Assert.Contains(":name:", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Lookup_UnknownKey_ErrorListsExamplesAndCount()
  {
    Result<Gitmoji> result = GitmojiCatalog.Lookup(":definitely_not_a_gitmoji:");
    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownEmojiKey", result.Error!.Code);
    // First three keys of the embedded table, in file order.
    Assert.Multiple(
        () => Assert.Contains(":tada:", result.Error!.Message, StringComparison.Ordinal),
        () => Assert.Contains(":art:", result.Error.Message, StringComparison.Ordinal),
        () => Assert.Contains(":sparkles:", result.Error.Message, StringComparison.Ordinal),
        () => Assert.Contains("66", result.Error.Message, StringComparison.Ordinal));
  }

  [Fact]
  public void Lookup_CaseSensitive_Rejected()
  {
    Result<Gitmoji> result = GitmojiCatalog.Lookup(":TADA:");
    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownEmojiKey", result.Error!.Code);
  }

  [Fact]
  public void All_KeysUnique() =>
      Assert.Equal(GitmojiCatalog.All.Count,
          GitmojiCatalog.All.Select(g => g.Key).Distinct(StringComparer.Ordinal).Count());

  [Fact]
  public void All_EmojiAndDescriptionsNonEmpty() =>
      Assert.All(GitmojiCatalog.All, g =>
      {
        Assert.NotEmpty(g.Emoji);
        Assert.NotEmpty(g.Description);
      });
}
