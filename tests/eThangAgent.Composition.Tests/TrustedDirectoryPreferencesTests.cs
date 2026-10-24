using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Strict parsing and resolution of the trusted-directory list (issue #96). The
/// stored shape is {path, enabled, editable}; anything else is a NAMED failure, and a
/// value that fails to parse resolves to NO roots at all - the safe direction for
/// external reach is nothing, never half a list nobody can audit.</summary>
public sealed class TrustedDirectoryPreferencesTests
{
  private const string Vault = @"C:\Users\me\vault";

  [Fact]
  public void Unconfigured_ParsesToEmpty()
  {
    Assert.Empty(Entries(null));
    Assert.Empty(Entries(@"   "));
  }

  [Fact]
  public void Unconfigured_ResolvesToNoRoots() =>
      Assert.Empty(TrustedDirectoryPreferences.Resolve(null));

  [Fact]
  public void ValidEntry_RoundTripsThroughSerialize()
  {
    string stored = TrustedDirectoryPreferences.Serialize(
        [new TrustedDirectoryEntry(Vault, Enabled: true, Editable: true)]);

    TrustedDirectoryEntry entry = Assert.Single(Entries(stored));

    Assert.Equal(Vault, entry.Path);
    Assert.True(entry.Enabled);
    Assert.True(entry.Editable);
  }

  [Fact]
  public void DisabledEntry_ParsesButResolvesToNothing()
  {
    string stored = TrustedDirectoryPreferences.Serialize(
        [new TrustedDirectoryEntry(Vault, Enabled: false, Editable: true)]);

    // The row is remembered (so the user's checkbox state survives) but grants nothing.
    _ = Assert.Single(Entries(stored));
    Assert.Empty(TrustedDirectoryPreferences.Resolve(stored));
  }

  [Fact]
  public void MissingEditable_DefaultsToReadOnly() =>
      // An entry written before the permission existed carries no 'editable' field;
      // read-only is the safe reading of silence.
      Assert.False(Assert.Single(Entries(Json(@"path=C:\vault,enabled=true"))).Editable);

  [Fact]
  public void SecondEntry_DoesNotAffectTheFirstEntriesPermission()
  {
    string stored = TrustedDirectoryPreferences.Serialize(
        [new TrustedDirectoryEntry(@"C:\a", Enabled: true, Editable: false),
         new TrustedDirectoryEntry(@"C:\b", Enabled: true, Editable: true)]);

    IReadOnlyList<TrustedDirectory> roots = TrustedDirectoryPreferences.Resolve(stored);

    Assert.Equal(2, roots.Count);
    Assert.False(roots[0].Editable);
    Assert.True(roots[1].Editable);
  }

  [Fact]
  public void NotJson_IsANamedFailure()
  {
    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(@"{not json");

    Assert.False(parsed.IsSuccess);
    Assert.Equal(@"InvalidTrustedDirectories", parsed.Error.Code);
  }

  [Fact]
  public void NotAnArray_IsANamedFailureNamingTheShape()
  {
    // An OBJECT is not the stored shape: the list is an array of entries. The literal
    // is assembled with the serializer so the path's backslashes escape exactly once.
    string objectJson = "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(Vault) + "}";

    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(objectJson);

    Assert.False(parsed.IsSuccess);
    Assert.Contains("JSON array of {path, enabled, editable} entries", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void BlankPath_IsANamedFailureNamingTheIndex()
  {
    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(Json(
        @"path=  ,enabled=true"));

    Assert.False(parsed.IsSuccess);
    Assert.Equal(@"InvalidTrustedDirectories", parsed.Error.Code);
    Assert.Contains(@"entry 1", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingEnabled_IsANamedFailureNamingTheRequiredFields()
  {
    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(Json(
        @"path=C:\v"));

    Assert.False(parsed.IsSuccess);
    Assert.Contains("a boolean 'enabled'", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void UnparseableValue_ResolvesToNoRoots() =>
      // A bad stored value never half-grants: the session opens with no external reach.
      Assert.Empty(TrustedDirectoryPreferences.Resolve(@"[not json"));

  [Fact]
  public void WorkspaceKey_IsWorkspaceScoped() =>
      // An approval says 'in THIS workspace the agent may reach this root'. A global
      // key would widen every other workspace's reach the moment one approved.
      Assert.Equal(@"trusted_directories:ws:C:\ws", TrustedDirectoryPreferences.WorkspaceKey(@"C:\ws"));

  [Fact]
  public void EnabledEntries_ResolveInStoredOrder()
  {
    string stored = TrustedDirectoryPreferences.Serialize(
        [new TrustedDirectoryEntry(@"C:\first", Enabled: true, Editable: false),
         new TrustedDirectoryEntry(@"C:\skipped", Enabled: false, Editable: true),
         new TrustedDirectoryEntry(@"C:\second", Enabled: true, Editable: true)]);

    IReadOnlyList<TrustedDirectory> roots = TrustedDirectoryPreferences.Resolve(stored);

    Assert.Equal([
        new TrustedDirectory(@"C:\first", Editable: false),
        new TrustedDirectory(@"C:\second", Editable: true),
    ], roots);
  }

  /// <summary>The parsed entries of a stored value, asserting the parse SUCCEEDED -
  ///     these tests care about CONTENT, and an unchecked failure would surface as a
  ///     confusing null-collection error instead of the parse's own message.</summary>
  private static IReadOnlyList<TrustedDirectoryEntry> Entries(string? stored)
  {
    Result<IReadOnlyList<TrustedDirectoryEntry>> parsed = TrustedDirectoryPreferences.Parse(stored);
    Assert.True(parsed.IsSuccess, parsed.Error?.Message);
    return parsed.Value;
  }

  /// <summary>Builds a ONE-entry array literal from 'name=value' pairs, so a test can
  ///     express a shape the serializer would never write - a missing field, a blank
  ///     path - without hand-escaping JSON. A bare true/false stays a boolean; anything
  ///     else becomes a string.</summary>
  private static string Json(string pairs) =>
      "[{" + string.Join(",", pairs.Split(',').Select(pair =>
      {
        string[] parts = pair.Split('=', 2);
        // A bare true/false stays a boolean; anything else becomes an ESCAPED JSON
        // string, so a Windows path's backslashes survive the trip into the document.
        string value = parts[1] is "true" or "false"
            ? parts[1]
            : $"\"{parts[1].Replace("\\", "\\\\", StringComparison.Ordinal)}\"";
        return $"\"{parts[0]}\":" + value;
      })) + "}]";
}
