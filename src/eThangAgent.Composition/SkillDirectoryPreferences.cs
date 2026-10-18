using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Strict parsing and durable keys for the configured skill directories
///     (skill-routing rework, Phase 1 Task 6). The stored value is a JSON array of
///     {path, enabled} objects - the SAME stored shape as the session-file lists
///     (<see cref="SessionFilePreferences"/>), reusing <see cref="SessionFileEntry"/>.
///     Anything that is not exactly that shape is a named failure: nothing is
///     silently coerced, defaulted, or dropped. Null/blank means unconfigured and
///     parses to an empty list.</summary>
public static class SkillDirectoryPreferences
{
  /// <summary>App-preference key holding the global skill directory list.</summary>
  public const string GlobalKey = "skill_directories:global";

  /// <summary>App-preference key prefix holding one workspace's skill directory list.
  ///     The full key is <c>skill_directories:ws:{workspaceRoot}</c> - the same
  ///     workspace-scoped shape the session-file lists use.</summary>
  public const string WorkspacePrefix = "skill_directories:ws:";

  /// <summary>The named failure code every parse error carries.</summary>
  private const string ErrorCode = "InvalidSkillDirectories";

  /// <summary>The noun the parse errors name this list by.</summary>
  private const string EntryNoun = "skill directory";

  public static string WorkspaceKey(string workspaceRoot) => WorkspacePrefix + workspaceRoot;

  /// <summary>Parses a stored preference value. Success carries the entries in
  ///     stored order; Failure carries the named <c>InvalidSkillDirectories</c>
  ///     error, whose message names the 1-based entry index. The parse validates
  ///     SHAPE only: duplicate-path resolution (a workspace path equal,
  ///     case-insensitively, to a global path) lives at the factory's resolution
  ///     site, where the global entry wins and the workspace duplicate is skipped.</summary>
  public static Result<IReadOnlyList<SessionFileEntry>> Parse(string? stored) =>
      PreferenceEntryListParser.Parse(stored, ErrorCode, EntryNoun);

  /// <summary>Serializes entries back to the stored JSON shape.</summary>
  public static string Serialize(IReadOnlyList<SessionFileEntry> entries) =>
      JsonSerializer.Serialize(entries);
}
