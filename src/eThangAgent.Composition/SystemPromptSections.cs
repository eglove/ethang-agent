using System.Globalization;

namespace eThangAgent.Composition;

/// <summary>Tag names and the renderer for the delimited sections of the system prompt.
///     Every section names its own source, so the harness's built-in text and each
///     configured file are told apart by their labels rather than by their position.</summary>
public static class SystemPromptSections
{
  /// <summary>Tag for the harness's own built-in text (skills bootstrap, tools mapping,
  ///     commit style). That text has no file source.</summary>
  public const string HarnessBootstrap = "harness-bootstrap";

  /// <summary>Tag for one configured session file.</summary>
  public const string SessionFile = "session-file";

  /// <summary>Renders one section: an opening tag carrying its source attributes, the
  ///     body verbatim, and the closing tag on its own line.</summary>
  public static string Render(string tag, string attributes, string body)
  {
    ArgumentNullException.ThrowIfNull(tag);
    ArgumentNullException.ThrowIfNull(attributes);
    ArgumentNullException.ThrowIfNull(body);
    return string.Create(CultureInfo.InvariantCulture,
        $"<{tag} {attributes}>{Environment.NewLine}{body.TrimEnd()}{Environment.NewLine}</{tag}>");
  }
}
