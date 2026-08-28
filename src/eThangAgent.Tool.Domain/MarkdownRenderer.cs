using System.Globalization;
using System.Text;

namespace eThangAgent.ToolDomain;

/// <summary>Pure markdown renderer over <see cref="MarkdownDocument"/> — verbatim port of
/// the reference markdown-generator semantics. Blocks join with blank lines; space blocks
/// emit Count+1 newlines and suppress the following separator; frontmatter sorts title-first;
/// YAML scalars quote only when containing ':' / '"' / '#'. Rendering is total: input
/// validation lives entirely in <see cref="MarkdownDocumentParser"/>, so malformed documents
/// never reach this class.</summary>
public static class MarkdownRenderer
{
  public static string Render(MarkdownDocument document)
  {
    ArgumentNullException.ThrowIfNull(document);
    StringBuilder result = new();

    if (document.FrontMatter is { Count: > 0 } fm)
    {
      _ = result.Append(RenderFrontMatter(fm));
    }

    bool hasWrittenBlock = false;
    bool lastWasSpace = false;

    foreach (MarkdownBlock? block in document.Blocks)
    {
      if (block is null)
      {
        continue;
      }

      if (block is SpaceBlock space)
      {
        _ = result.Append('\n', space.Count + 1);
        lastWasSpace = true;
        continue;
      }

      if (hasWrittenBlock && !lastWasSpace)
      {
        _ = result.Append("\n\n");
      }

      _ = result.Append(RenderBlock(block));
      hasWrittenBlock = true;
      lastWasSpace = false;
    }

    if (result.Length > 0 && result[^1] != '\n')
    {
      _ = result.Append('\n');
    }

    return result.ToString();
  }

  private static string RenderBlock(MarkdownBlock block) => block switch
  {
    HeaderBlock h => new string('#', h.Level) + " " + h.Text,
    TextBlock t => t.Text,
    QuoteBlock q => PrefixLines(q.Text, "> "),
    AlertBlock a => "> [!" + AlertWord(a.Alert) + "]\n" + PrefixLines(a.Text, "> "),
    CodeBlock c => "```" + (c.Language ?? "") + "\n" + c.Code + "\n```",
    ListBlock l => RenderList(l.Items, l.Kind, level: 0),
    TaskListBlock tl => string.Join("\n", tl.Items.Select(i => (i.IsComplete ? "[X] " : "[ ] ") + i.Label)),
    TableBlock tb => RenderTable(tb),
    SpaceBlock => throw new InvalidOperationException("space blocks are handled inline"), // unreachable
    _ => throw new InvalidOperationException($"Unknown block type: {block.GetType().Name}"),
  };

  private static string AlertWord(AlertType type) => type switch
  {
    AlertType.Caution => "CAUTION",
    AlertType.Important => "IMPORTANT",
    AlertType.Note => "NOTE",
    AlertType.Tip => "TIP",
    AlertType.Warning => "WARNING",
    _ => throw new InvalidOperationException($"Unknown alert type: {type}"),
  };

  private static string PrefixLines(string text, string prefix) =>
      string.Join("\n", text.Split('\n').Select(line => prefix + line));

  private static string RenderList(IReadOnlyList<ListItem> items, ListKind kind, int level)
  {
    string prefix = kind == ListKind.Unordered ? "* " : "1. ";
    string indent = new('\t', level);
    List<string> lines = [];

    foreach (ListItem item in items)
    {
      lines.Add(indent + prefix + item.Text);
      if (item.Children is { Count: > 0 } children)
      {
        lines.Add(RenderList(children, kind, level + 1));
      }
    }

    return string.Join("\n", lines);
  }

  private static string RenderTable(TableBlock table)
  {
    int headerCount = table.Headers.Count;
    List<string> lines = [];

    foreach (IReadOnlyList<string> row in table.Rows)
    {
      if (row.Count != headerCount)
      {
        throw new InvalidOperationException(
            $"Table row cell count ({row.Count}) does not match header count ({headerCount}).");
      }

      lines.Add("| " + string.Join(" | ", row) + " |");
    }

    string headerLine = "| " + string.Join(" | ", table.Headers.Select(h => h.Text)) + " |";
    string dividerLine = "| " + string.Join(" | ", table.Headers.Select(Divider)) + " |";

    return string.Join("\n", new[] { headerLine, dividerLine }.Concat(lines));
  }

  private static string Divider(TableHeader header) => header.Align switch
  {
    TableAlign.Left => ":---",
    TableAlign.Center => ":---:",
    TableAlign.Right => "---:",
    _ => "---",
  };

  private static string RenderFrontMatter(IReadOnlyDictionary<string, object> frontMatter)
  {
    List<string> sortedKeys = [.. frontMatter.Keys
        .OrderBy(k => k == "title" ? 0 : 1)
        .ThenBy(k => k, StringComparer.CurrentCulture)];

    List<string> lines = [];
    foreach (string? key in sortedKeys)
    {
      object? value = frontMatter[key];
      if (value is null)
      {
        continue;
      }

      string valueString = ScalarString(value);
      if (valueString.Contains('\n', StringComparison.Ordinal))
      {
        throw new InvalidOperationException(
            $"Frontmatter value for \"{key}\" contains a newline; multi-line values are not allowed.");
      }

      lines.Add(key + ": " + YamlScalar(valueString));
    }

    return "---\n" + string.Join("\n", lines) + "\n---\n";
  }

  private static string ScalarString(object value) => value switch
  {
    bool b => b ? "true" : "false",
    double d => d % 1 == 0 && Math.Abs(d) < 9.22e18 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
    float f => f.ToString(CultureInfo.InvariantCulture),
    int i => i.ToString(CultureInfo.InvariantCulture),
    long l => l.ToString(CultureInfo.InvariantCulture),
    decimal m => m.ToString(CultureInfo.InvariantCulture),
    _ => value.ToString() ?? "",
  };

  private static string YamlScalar(string value)
  {
    if (!value.Contains(':', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) && !value.Contains('#', StringComparison.Ordinal))
    {
      return value;
    }

    string escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    return "\"" + escaped + "\"";
  }
}
