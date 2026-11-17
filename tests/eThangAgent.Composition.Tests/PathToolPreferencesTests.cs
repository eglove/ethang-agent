using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>Strict parsing of the ticked-tool list. The stored shape is a JSON array of
/// bare tool names; anything else is a NAMED failure, because a name nobody wrote must
/// never reach the bootstrap as if the user had chosen it.</summary>
public sealed class PathToolPreferencesTests
{
  [Fact]
  public void Unconfigured_ParsesToEmpty()
  {
    Assert.Empty(Names(null));
    Assert.Empty(Names(string.Empty));
    Assert.Empty(Names("   "));
  }

  [Fact]
  public void Key_IsTheAppScopedPathToolsKey() =>
      Assert.Equal("path_tools", PathToolPreferences.Key);

  [Fact]
  public void Names_KeepStoredOrder() =>
      Assert.Equal(["rg", "jq"], Names("""["rg","jq"]"""));

  [Fact]
  public void Duplicate_KeepsTheFirstSpelling()
  {
    IReadOnlyList<string> names = Names("""["rg","RG"]""");

    string name = Assert.Single(names);
    Assert.Equal("rg", name);
  }

  [Fact]
  public void ExecutableExtensions_AreStripped()
  {
    Assert.Equal(["rg"], Names("""["rg.exe"]"""));
    Assert.Equal(["npm"], Names("""["npm.cmd"]"""));
    Assert.Equal(["build"], Names("""["build.BAT"]"""));
  }

  [Fact]
  public void NonExecutableExtension_IsPartOfTheName() =>
      // Only the three spawnable extensions are stripped; any other suffix is the name.
      Assert.Equal(["tool.py"], Names("""["tool.py"]"""));

  [Fact]
  public void BlankElement_IsANamedFailureNamingTheIndex()
  {
    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse("""["rg",""]""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidPathTools", parsed.Error.Code);
    Assert.Contains("entry 2", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NonStringElement_IsANamedFailureNamingTheIndex()
  {
    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse("""["rg",7]""");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidPathTools", parsed.Error.Code);
    Assert.Contains("entry 2", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void JsonObject_IsANamedFailureNamingTheShape()
  {
    string objectJson = JsonSerializer.Serialize(new Dictionary<string, bool> { ["rg"] = true });

    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse(objectJson);

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidPathTools", parsed.Error.Code);
    Assert.Contains("JSON array of tool name strings", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NotJson_IsANamedFailure()
  {
    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse("not json");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidPathTools", parsed.Error.Code);
  }

  [Fact]
  public void Serialize_ThenParse_RoundTripsWithoutLoss()
  {
    string[] names = ["rg", "jq", "fzf"];

    Assert.Equal(names, Names(PathToolPreferences.Serialize(names)));
  }

  [Fact]
  public void Serialize_StripsExtensionsAndDuplicates() =>
      // The stored value is bare and de-duplicated whatever the caller hands over, so a
      // value written by the page and one written by hand parse identically.
      Assert.Equal(["rg", "npm"], Names(PathToolPreferences.Serialize(["rg.exe", "RG", "npm.cmd"])));

  [Fact]
  public void Serialize_RejectsABlankName() =>
      // A blank name is a caller defect rather than stored input, so it fails loudly
      // instead of being written as a value that could never resolve.
      Assert.Throws<ArgumentException>(() => PathToolPreferences.Serialize(["rg", " "]));

  private static IReadOnlyList<string> Names(string? stored)
  {
    Result<IReadOnlyList<string>> parsed = PathToolPreferences.Parse(stored);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    return parsed.Value;
  }
}
