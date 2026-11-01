using System.Globalization;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Injects the workspace root and the verbatim contents of the user's
///     CONFIGURED session files (global list, then workspace list; enabled entries
///     only) into the system prompt. Each file renders as its own section naming its
///     scope and its path, so the harness text above it and every other file are told
///     apart by label. An unreadable or missing configured file is dropped with a
///     visible annotation - never a silent skip, never a session failure. Files above
///     the size cap render truncated with a marker. Nothing configured renders nothing
///     (truly empty by default).
///     <para>Every file read happens in the CONSTRUCTOR and <see cref="Build"/> renders
///     the snapshot taken there, so a session file's content is fixed for the life of
///     the session - exactly like the workspace root it is rendered beside.</para></summary>
public sealed class SessionFilesPromptProvider : ISystemPromptProvider
{
  /// <summary>Per-file render cap: files above this render truncated with a marker.
  ///     A prompt-size guard, not a security boundary - char-based by design.</summary>
  public const int MaxFileChars = 512 * 1024;

  private readonly string _root;
  private readonly List<string> _annotations;
  private readonly List<string> _blocks;

  public SessionFilesPromptProvider(string root, string? globalStored, string? workspaceStored)
  {
    _root = Path.GetFullPath(root);
    List<string> annotations = [];
    List<string> blocks = [];
    AppendScope("global", globalStored, annotations, blocks);
    AppendScope("workspace", workspaceStored, annotations, blocks);
    _annotations = annotations;
    _blocks = blocks;
  }

  /// <summary>Pure render over the construction-time snapshot: identical output for
  ///     every call, no I/O.</summary>
  public string Build()
  {
    if (_annotations.Count == 0 && _blocks.Count == 0)
    {
      return string.Empty;
    }

    StringBuilder prompt = new();
    _ = prompt.AppendLine(string.Create(InvariantCulture, $"Working directory: {_root}"));
    if (_blocks.Count > 0)
    {
      // The lead-in names both scopes; each block's tag carries its own scope and path.
      _ = prompt.AppendLine(
          "Configured session files are quoted below, each in its own section: a global file " +
          "applies to every workspace, a workspace file only to this one.");
    }

    foreach (string annotation in _annotations)
    {
      _ = prompt.AppendLine(annotation);
    }

    foreach (string block in _blocks)
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

      blocks.Add(SystemPromptSections.Render(SystemPromptSections.SessionFile,
          string.Create(InvariantCulture, $"scope=\"{scopeName}\" path=\"{entry.Path}\""), contents));
    }
  }

  private static CultureInfo InvariantCulture => CultureInfo.InvariantCulture;
}
