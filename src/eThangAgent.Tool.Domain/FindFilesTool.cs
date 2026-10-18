using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>find_files: name-pattern file search over the workspace. One call answers
///     "where is the file named X" — the recurring three-probe pattern (list everything,
///     list folders, enumerate the tree) is what this removes. Input is strict: pattern
///     mandatory (Win32 glob, * and ?); optional workspace-relative path scopes the
///     search root; optional maxResults truncates with a visible marker. Output begins
///     with an annotation line — metadata, not content.</summary>
public sealed class FindFilesTool(IPathResolver resolver, IFileSearchAccess search)
    : ITool, IWorkspaceScopedTool
{
  private const int DefaultMaxResults = 100;
  private const int MaxResultsCap = 500;

  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSearchAccess _search = search ?? throw new ArgumentNullException(nameof(search));

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new FindFilesTool(new WorkspacePathResolver(workspaceRoot), _search);

  public ToolDefinition Definition { get; } = new(
      "find_files",
      "Search the workspace for files by name pattern (Win32 glob: * and ?). timeoutSeconds and pattern are mandatory; optional path scopes the search to a workspace-relative subdirectory, optional maxResults (default 100, cap 500) bounds the output. Output begins with an annotation line in [brackets] — it is metadata, not content — followed by workspace-relative paths, one per line. Excludes build output and VCS internals automatically. Prefer this over enumerating the whole tree when looking for files by name.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("pattern", ToolParameterType.Text,
              "File name pattern with * and ? wildcards, e.g. \"*.cs\" or \"AgentView*\". Matches file names only, not paths."),
          new ToolParameter("path", ToolParameterType.Text,
              "Optional workspace-relative subdirectory to scope the search to."),
          new ToolParameter("maxResults", ToolParameterType.WholeNumber,
              "Optional cap on listed matches (default 100, maximum 500).", Minimum: 1),
      ]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<JsonElement> parsed = ToolArguments.ParseObject(input.JsonArguments);
    return !parsed.IsSuccess
      ? Task.FromResult(Err(parsed.Error))
      : RunParsed(input, parsed.Value, ct);
  }

  private Task<ToolResult> RunParsed(RawToolInput input, JsonElement json, CancellationToken ct)
  {
    if (ToolArguments.RejectUnknownParameters(json, ToolTimeout.ParameterName, "pattern", "path", "maxResults") is { } unknown)
    {
      return Task.FromResult(Err(unknown));
    }

    if (TryGetPattern(json, out string pattern) is { } patternError)
    {
      return Task.FromResult(Err(patternError));
    }

    if (ResolveScopedRoot(json, out string scopedRoot) is { } rootError)
    {
      return Task.FromResult(Err(rootError));
    }

    if (TryGetMaxResults(json, out int maxResults) is { } maxError)
    {
      return Task.FromResult(Err(maxError));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => SearchAsync(pattern, scopedRoot, maxResults, token), ct);
  }

  private static DomainError? TryGetPattern(JsonElement json, out string pattern)
  {
    pattern = string.Empty;
    if (!json.TryGetProperty("pattern", out JsonElement patternEl) || patternEl.ValueKind != JsonValueKind.String)
    {
      return new DomainError("InvalidParameter",
          "'pattern' is required and must be a string (Win32 glob: * and ?).");
    }

    pattern = patternEl.GetString()!.Trim();
    return pattern.Length == 0
      ? new DomainError("InvalidParameter", "'pattern' must not be empty.")
      : null;
  }

  private DomainError? ResolveScopedRoot(JsonElement json, out string scopedRoot)
  {
    scopedRoot = string.Empty;
    bool hasPath = json.TryGetProperty("path", out JsonElement pathEl);
    if (!hasPath)
    {
      return ResolveRoot(".", out scopedRoot);
    }

    DomainError? kind = pathEl.ValueKind == JsonValueKind.String
      ? null
      : new DomainError("InvalidParameter", "'path' must be a string.");
    return kind ?? ResolveRoot(pathEl.GetString()!, out scopedRoot);
  }

  private DomainError? ResolveRoot(string path, out string scopedRoot)
  {
    Result<string> resolved = _resolver.Resolve(path);
    scopedRoot = resolved.IsSuccess ? resolved.Value : string.Empty;
    return resolved.IsSuccess ? null : resolved.Error;
  }

  private static DomainError? TryGetMaxResults(JsonElement json, out int maxResults)
  {
    maxResults = DefaultMaxResults;
    if (!json.TryGetProperty("maxResults", out JsonElement maxEl))
    {
      return null;
    }

    if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out int requested) || requested < 1)
    {
      return new DomainError("InvalidParameter",
          "'maxResults' must be a positive whole number.");
    }

    maxResults = Math.Min(requested, MaxResultsCap);
    return null;
  }

  private async Task<ToolResult> SearchAsync(string pattern, string scopedRoot, int maxResults, CancellationToken ct)
  {
    Result<IReadOnlyList<string>> found = await _search.EnumerateFilesAsync(scopedRoot, pattern, recurse: true, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Err(found.Error);
    }

    IReadOnlyList<string> matches = found.Value;
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[find_files pattern '{pattern}' -> {matches.Count} matches]");
    int shown = Math.Min(matches.Count, maxResults);
    for (int i = 0; i < shown; i++)
    {
      _ = sb.AppendLine(MakeRelative(scopedRoot, matches[i]));
    }

    if (matches.Count > shown)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"[warning] showing {shown} of {matches.Count} matches; refine the pattern");
    }

    return new ToolResult(sb.ToString().TrimEnd(), false);
  }

  private static string MakeRelative(string scopedRoot, string path)
  {
    try
    {
      string relative = Path.GetRelativePath(scopedRoot, path);
      return relative.Length == 0 ? "." : relative;
    }
    catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
    {
      return path;
    }
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
