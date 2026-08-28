using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class SearchTool(IPathResolver resolver, ISearchAccess search) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly ISearchAccess _search = search ?? throw new ArgumentNullException(nameof(search));

  public ToolDefinition Definition { get; } = new(
      "search_files",
      "Search workspace text files for a pattern. timeoutSeconds, pattern, mode, and maxResults are mandatory; " +
      "mode is exactly 'Literal' or 'Regex'; path optionally scopes to a subdirectory (defaults to " +
      "the workspace root); glob optionally filters filenames like '*.cs'. maxResults above 200 " +
      "clamps to 200 with a visible warning rather than failing. Binary files and .git contents are " +
      "skipped. Output begins with an annotation line `[search ...]` giving match and scan counts; " +
      "each matching file follows under a `--- path ---` header with line-numbered, arrow-prefixed " +
      "lines (numbers and arrows are never part of the content). A trailing `[warning]` means more " +
      "matches exist beyond the cap. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("pattern", ToolParameterType.Text,
                "Text to find (Literal) or regular expression (Regex)."),
            new ToolParameter("mode", ToolParameterType.Text,
                "Exactly 'Literal' or 'Regex'."),
            new ToolParameter("path", ToolParameterType.Text,
                "Optional subdirectory scope; defaults to the workspace root."),
            new ToolParameter("glob", ToolParameterType.Text,
                "Optional filename filter, e.g. '*.cs'."),
            new ToolParameter("maxResults", ToolParameterType.WholeNumber,
                "Maximum matches returned; values above 200 clamp with a warning. Minimum: 1", Minimum: 1),
            new ToolParameter("contextLines", ToolParameterType.WholeNumber,
                "Context lines around each match. Minimum: 0", Minimum: 0),
      ],
      ["timeoutSeconds", "pattern", "mode", "maxResults"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<SearchToolInput> parsed = SearchToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    SearchToolInput v = parsed.Value;

    Result<string> scope = _resolver.Resolve(v.Path ?? ".");
    if (!scope.IsSuccess)
    {
      return Task.FromResult(Err(scope.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        SearchAsync(scope.Value, v, token), ct);
  }

  private async Task<ToolResult> SearchAsync(string scope, SearchToolInput v, CancellationToken ct)
  {
    Result<FileSearch> found = await _search.SearchFilesAsync(
        scope, v.Pattern, v.Regex, v.Glob, v.MaxResults, v.ContextLines, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Err(found.Error);
    }

    FileSearch o = found.Value;

    StringBuilder sb = new();
    if (o.Matches.Count == 0)
    {
      return new ToolResult(
          $"[search '{v.Pattern}' under {scope}: no matches ({o.FilesScanned} files scanned)]",
          false);
    }

    int fileCount = o.Matches.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[search '{v.Pattern}' under {scope}: {o.Matches.Count} match(es) " +
              $"across {fileCount} file(s), {o.FilesScanned} files scanned]\n");
    foreach (IGrouping<string, SearchMatch> group in o.Matches.GroupBy(m => m.Path, StringComparer.Ordinal))
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"--- {Path.GetRelativePath(scope, group.Key)} ---\n");
      foreach (SearchMatch? m in group)
      {
        // The window may start above the match line when context lines reach
        // the top of the file; anchor numbering at the match's window position.
        int matchIndex = Math.Min(m.LineNumber - 1, v.ContextLines);
        int firstLineNumber = m.LineNumber - matchIndex;
        int width = (firstLineNumber + m.Lines.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        for (int j = 0; j < m.Lines.Count; j++)
        {
          _ = sb.Append(CultureInfo.InvariantCulture, $"{(firstLineNumber + j).ToString(CultureInfo.InvariantCulture).PadLeft(width)}\u2192 {m.Lines[j]}\n");
        }
      }
    }

    string text = sb.ToString();
    if (o.Truncated)
    {
      text += $"[warning] results capped at {v.MaxResults} matches; " +
              "narrow with pattern/path/glob to see more";
    }
    else
    {
      text = text.TrimEnd('\n');
    }

    return new ToolResult(text, false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
