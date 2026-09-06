using System.Globalization;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Injects the workspace root and the verbatim contents of the user's
///     CONFIGURED session files (global list, then workspace list; enabled entries
///     only) into the system prompt. This replaces the hardcoded AGENTS.md read: the
///     user decides what loads, per scope, through app preferences. An unreadable or
///     missing configured file is dropped with a visible annotation - never a silent
///     skip, never a session failure. Files above the size cap render truncated with
///     a marker. Nothing configured renders nothing (truly empty by default).
///     Store reads happen at container build; Build() is a pure render.</summary>
public sealed class SessionFilesPromptProvider(string root, string? globalStored, string? workspaceStored)
  : ISystemPromptProvider
{
  /// <summary>Per-file render cap: files above this render truncated with a marker.
  ///     A prompt-size guard, not a security boundary - char-based by design.</summary>
  public const int MaxFileChars = 512 * 1024;

  private readonly string _root = Path.GetFullPath(root);
  private readonly string? _globalStored = globalStored;
  private readonly string? _workspaceStored = workspaceStored;

  public string Build()
  {
    List<string> annotations = [];
    List<string> blocks = [];
    AppendScope("global", _globalStored, annotations, blocks);
    AppendScope("workspace", _workspaceStored, annotations, blocks);

    if (annotations.Count == 0 && blocks.Count == 0)
    {
      return string.Empty;
    }

    StringBuilder prompt = new();
    _ = prompt.AppendLine(string.Create(InvariantCulture, $"Working directory: {_root}"));
    if (blocks.Count > 0)
    {
      _ = prompt.AppendLine("Its configured session files have been read; verbatim contents follow.");
    }

    foreach (string annotation in annotations)
    {
      _ = prompt.AppendLine(annotation);
    }

    foreach (string block in blocks)
    {
      _ = prompt.AppendLine();
      _ = prompt.AppendLine(block);
    }

    return prompt.ToString();
  }

  private static void AppendScope(string scopeName, string? stored, List<string> annotations, List<string> blocks)
  {
    if (string.IsNullOrWhiteSpace(stored))
    {
      return;
    }

    Result<IReadOnlyList<SessionFileEntry>> parsed = SessionFilePreferences.Parse(stored);
    if (!parsed.IsSuccess)
    {
      annotations.Add(string.Create(InvariantCulture, $"[session-files] {scopeName} list ignored: {parsed.Error.Message}"));
      return;
    }

    foreach (SessionFileEntry entry in parsed.Value)
    {
      if (!entry.Enabled)
      {
        continue;
      }

      if (!File.Exists(entry.Path))
      {
        annotations.Add(string.Create(InvariantCulture, $"[session-files] configured file not found, skipped: {entry.Path}"));
        continue;
      }

      string contents;
      try
      {
        contents = File.ReadAllText(entry.Path);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
      {
        annotations.Add(string.Create(InvariantCulture, $"[session-files] configured file unreadable, skipped: {entry.Path} ({ex.Message})"));
        continue;
      }

      if (contents.Length > MaxFileChars)
      {
        contents = string.Create(InvariantCulture, $"{contents[..MaxFileChars]}{Environment.NewLine}[session-files] truncated at {MaxFileChars / 1024} KB");
      }

      blocks.Add(string.Create(InvariantCulture, $"<session-file source=\"{entry.Path}\">{contents.TrimEnd()}{Environment.NewLine}</session-file>"));
    }
  }

  private static CultureInfo InvariantCulture => CultureInfo.InvariantCulture;
}
