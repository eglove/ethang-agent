// The raw JSON literals below are the unit under test: the parser's strictness
// against hand-written shapes (wrong case, plain strings, malformed arrays).
#pragma warning disable JSON002
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>E (session-start file reads by setting): the preference value is a JSON
///     array of {path, enabled} entries - checkboxes included. Parsing is strict:
///     anything that is not exactly that shape is a named failure, never a silent
///     partial read. Null/blank means unconfigured and parses to an empty list.</summary>
public class SessionFilePreferencesTests
{
  [Fact]
  public void Null_Or_Blank_Parses_To_An_Empty_List()
  {
    Assert.True(SessionFilePreferences.Parse(null).IsSuccess);
    Assert.True(SessionFilePreferences.Parse("").IsSuccess);
    Assert.Empty(SessionFilePreferences.Parse(null).Value!);
  }

  [Fact]
  public void Valid_Array_Parses_Path_And_Enabled()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed = SessionFilePreferences.Parse(
        @"[{""path"":""C:\\ws\\AGENTS.md"",""enabled"":true},{""path"":""C:\\ws\\NOTES.md"",""enabled"":false}]");
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    Assert.Equal(2, parsed.Value.Count);
    Assert.Equal("C:\\ws\\AGENTS.md", parsed.Value[0].Path);
    Assert.True(parsed.Value[0].Enabled);
    Assert.False(parsed.Value[1].Enabled);
  }

  [Fact]
  public void Plain_String_Array_Is_Rejected_Strictly()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed =
        SessionFilePreferences.Parse(@"[""C:\\ws\\AGENTS.md""]");
    Assert.False(parsed.IsSuccess);
    Assert.Contains("session file", parsed.Error?.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Non_Array_Json_Is_A_Named_Failure()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed =
        SessionFilePreferences.Parse(@"{""path"":""x""}");
    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void Entry_Without_Enabled_Flag_Is_A_Named_Failure()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed =
        SessionFilePreferences.Parse(@"[{""path"":""C:\\x.md""}]");
    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void Entry_With_Blank_Path_Is_A_Named_Failure()
  {
    Result<IReadOnlyList<SessionFileEntry>> parsed =
        SessionFilePreferences.Parse(@"[{""path"":"""",""enabled"":true}]");
    Assert.False(parsed.IsSuccess);
  }

  [Fact]
  public void Preference_Keys_Derive_From_Scope()
  {
    Assert.Equal("session_files:global", SessionFilePreferences.GlobalKey);
    Assert.Equal("session_files:ws:C:\\proj", SessionFilePreferences.WorkspaceKey("C:\\proj"));
  }
}
#pragma warning restore JSON002
