using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Skill.Domain.Tests;

/// <summary>skills.sh JSON API parser (plan #29 task 4, adapted per ledger
/// v57): the site exposes GET /api/search?q= returning typed skill entries;
/// the fixture pins the captured live shape.</summary>
public class SkillsShSearchParserTests
{
  private static string FixtureJson()
  {
    string[] lines = File.ReadAllLines(FixturePath());
    return string.Join('\n', lines.Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
  }

  private static string FixturePath() =>
      Path.Combine(AppContext.BaseDirectory, "Fixtures", "skillssh-search.json");

  [Fact]
  public void Fixture_Parses_EntriesWithAddressNameInstalls()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch(FixtureJson());
    Assert.True(r.IsSuccess);
    Assert.True(r.Value.Count >= 4);
    Assert.Contains(r.Value, e => e.Address == "vercel-labs/agent-skills/deploy-to-vercel" && e.Installs == 137052L);
    Assert.Contains(r.Value, e => e.Address == "microsoft/azure-skills/azure-deploy" && e.Installs == 599402L);
  }

  [Fact]
  public void Fixture_EntryName_ComesFromTheNameField()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch(FixtureJson());
    Assert.True(r.IsSuccess);
    SkillsShEntry first = r.Value[0];
    Assert.Equal("deploy-to-vercel", first.Name);
  }

  [Fact]
  public void DuplicateAddresses_DedupedPreservingOrder()
  {
    string json = "{\"skills\":[{\"id\":\"o/r/a\",\"name\":\"a\",\"installs\":1},{\"id\":\"o/r/b\",\"name\":\"b\",\"installs\":2},{\"id\":\"o/r/a\",\"name\":\"a\",\"installs\":3}]}";
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch(json);
    Assert.True(r.IsSuccess);
    Assert.Equal(["o/r/a", "o/r/b"], [.. r.Value.Select(e => e.Address)]);
    Assert.Equal(1L, r.Value[0].Installs);
  }

  [Fact]
  public void EmptySkillsArray_Succeeds_WithNoEntries()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch("{\"skills\":[]}");
    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value);
  }

  [Fact]
  public void MalformedJson_FailsParseFailed()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch("not json at all");
    Assert.False(r.IsSuccess);
    Assert.Equal("ParseFailed", r.Error.Code);
  }

  [Fact]
  public void MissingSkillsKey_FailsParseFailed()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch("{\"query\":\"q\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal("ParseFailed", r.Error.Code);
  }

  [Fact]
  public void SkillsNotAnArray_FailsParseFailed()
  {
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch("{\"skills\":\"x\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal("ParseFailed", r.Error.Code);
  }

  [Fact]
  public void EntryWithMissingFields_SkippedQuietly()
  {
    string json = "{\"skills\":[{\"id\":\"o/r/ok\",\"name\":\"ok\",\"installs\":7},{\"name\":\"incomplete\"}]}";
    Result<IReadOnlyList<SkillsShEntry>> r = SkillsShSearchParser.ParseSearch(json);
    Assert.True(r.IsSuccess);
    SkillsShEntry one = Assert.Single(r.Value);
    Assert.Equal("o/r/ok", one.Address);
  }
}