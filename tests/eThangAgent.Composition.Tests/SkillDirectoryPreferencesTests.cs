// The raw JSON literals below are the unit under test: the parser's strictness
// against hand-written shapes (wrong case, plain strings, malformed arrays).
#pragma warning disable JSON002
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>Skill-directory preferences (Task 6, skill-routing rework Phase 1): the
///     preference value is a JSON array of {path, enabled} entries - the SAME stored
///     shape as the session-file lists. Parsing is strict: anything that is not
///     exactly that shape is a named failure, never a silent partial read.
///     Null/blank means unconfigured and parses to an empty list. The parse
///     validates SHAPE only - duplicate-path resolution (a workspace path equal,
///     case-insensitively, to a global path) lives at the factory's resolution
///     site, where the global entry wins and the workspace duplicate is skipped.</summary>
public class SkillDirectoryPreferencesTests
{
  [Fact]
  public void Parse_NullOrBlank_EmptyList()
  {
    Assert.True(SkillDirectoryPreferences.Parse(null).IsSuccess);
    Assert.True(SkillDirectoryPreferences.Parse("").IsSuccess);
    Assert.True(SkillDirectoryPreferences.Parse("   ").IsSuccess);
    Assert.Empty(SkillDirectoryPreferences.Parse(null).Value!);
  }

  [Fact]
  public void Parse_ValidEntries_PreservesOrder()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(
        @"[{""path"":""C:\\skill-a\\skills\\"",""enabled"":true},{""path"":""C:\\skill-b\\"",""enabled"":false},{""path"":""C:\\skill-c\\"",""enabled"":true}]");
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    Assert.Equal(3, parsed.Value.Count);
    Assert.Equal("C:\\skill-a\\skills\\", parsed.Value[0].Path);
    Assert.True(parsed.Value[0].Enabled);
    Assert.Equal("C:\\skill-b\\", parsed.Value[1].Path);
    Assert.False(parsed.Value[1].Enabled);
    Assert.Equal("C:\\skill-c\\", parsed.Value[2].Path);
    Assert.True(parsed.Value[2].Enabled);
  }

  [Fact]
  public void Parse_NonArray_FailsNamed()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(@"{""path"":""x""}");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSkillDirectories", parsed.Error?.Code);
    Assert.Contains("skill directory", parsed.Error?.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Parse_NonObjectEntry_FailsNamesIndex()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(@"[""C:\\skills\\x\\SKILL.md""]");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSkillDirectories", parsed.Error?.Code);
    Assert.Contains("entry 1", parsed.Error?.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_MissingPath_FailsNamesIndex()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(@"[{""enabled"":true}]");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSkillDirectories", parsed.Error?.Code);
    Assert.Contains("entry 1", parsed.Error?.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_BlankPath_FailsNamesIndex()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(@"[{""path"":"" "",""enabled"":true}]");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSkillDirectories", parsed.Error?.Code);
    Assert.Contains("entry 1", parsed.Error?.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_NonBoolEnabled_FailsNamesIndex()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(@"[{""path"":""C:\\skills\\x\\"",""enabled"":""yes""}]");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSkillDirectories", parsed.Error?.Code);
    Assert.Contains("entry 1", parsed.Error?.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_RoundTrips()
  {
    List<SessionFileEntry> entries = [new("C:\\skills\\deploy\\", true), new("C:\\skills\\review\\", false)];
    string stored = SkillDirectoryPreferences.Serialize(entries);
    Result<IReadOnlyList<SessionFileEntry>> parsed = SkillDirectoryPreferences.Parse(stored);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    Assert.Equal(entries.Count, parsed.Value.Count);
    Assert.Equal(entries[0], parsed.Value[0]);
    Assert.Equal(entries[1], parsed.Value[1]);
  }
}
#pragma warning restore JSON002
