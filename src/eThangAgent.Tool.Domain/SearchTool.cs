using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class SearchTool : ITool
{
    private readonly IPathResolver _resolver;
    private readonly ISearchAccess _search;

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
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("pattern", ToolParameterType.String,
                "Text to find (Literal) or regular expression (Regex)."),
            new ToolParameter("mode", ToolParameterType.String,
                "Exactly 'Literal' or 'Regex'."),
            new ToolParameter("path", ToolParameterType.String,
                "Optional subdirectory scope; defaults to the workspace root."),
            new ToolParameter("glob", ToolParameterType.String,
                "Optional filename filter, e.g. '*.cs'."),
            new ToolParameter("maxResults", ToolParameterType.Integer,
                "Maximum matches returned; values above 200 clamp with a warning. Minimum: 1", Minimum: 1),
            new ToolParameter("contextLines", ToolParameterType.Integer,
                "Context lines around each match. Minimum: 0", Minimum: 0),
        ],
        ["timeoutSeconds", "pattern", "mode", "maxResults"]);

    public SearchTool(IPathResolver resolver, ISearchAccess search)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = SearchToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));
        var v = parsed.Value!;

        var scope = _resolver.Resolve(v.Path ?? ".");
        if (!scope.IsSuccess)
            return Task.FromResult(Err(scope.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            SearchAsync(scope.Value!, v, token), ct);
    }

    private async Task<ToolResult> SearchAsync(string scope, SearchToolInput v, CancellationToken ct)
    {
        var found = await _search.SearchFilesAsync(
            scope, v.Pattern, v.Regex, v.Glob, v.MaxResults, v.ContextLines, ct);
        if (!found.IsSuccess)
            return Err(found.Error!);
        var o = found.Value!;

        var sb = new System.Text.StringBuilder();
        if (o.Matches.Count == 0)
        {
            return new ToolResult(
                $"[search '{v.Pattern}' under {scope}: no matches ({o.FilesScanned} files scanned)]",
                false);
        }

        var fileCount = o.Matches.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count();
        sb.Append($"[search '{v.Pattern}' under {scope}: {o.Matches.Count} match(es) " +
                  $"across {fileCount} file(s), {o.FilesScanned} files scanned]\n");
        foreach (var group in o.Matches.GroupBy(m => m.Path, StringComparer.Ordinal))
        {
            sb.Append($"--- {Path.GetRelativePath(scope, group.Key)} ---\n");
            foreach (var m in group)
            {
                // The window may start above the match line when context lines reach
                // the top of the file; anchor numbering at the match's window position.
                var matchIndex = Math.Min(m.LineNumber - 1, v.ContextLines);
                var firstLineNumber = m.LineNumber - matchIndex;
                var width = (firstLineNumber + m.Lines.Count - 1).ToString().Length;
                for (var j = 0; j < m.Lines.Count; j++)
                    sb.Append($"{(firstLineNumber + j).ToString().PadLeft(width)}\u2192 {m.Lines[j]}\n");
            }
        }

        var text = sb.ToString();
        if (o.Truncated)
            text += $"[warning] results capped at {v.MaxResults} matches; " +
                    "narrow with pattern/path/glob to see more";
        else
            text = text.TrimEnd('\n');
        return new ToolResult(text, false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
