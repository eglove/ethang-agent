namespace eThangAgent.ToolDomain;

/// <summary>A tool's advertised contract: name, description (a format contract the
///     model reads verbatim), and parameters. <see cref="RequiredParameters"/> lists the
///     names the input parser actually demands — it must match parser behavior exactly,
///     so advertisements never lie about requiredness. Defaults to every parameter name;
///     tools with optional parameters state the required subset explicitly.</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    IReadOnlyList<string>? RequiredParameters = null,
    bool MutatesWorkspace = false)
{
  /// <summary>Parameter names the caller must supply; defaults to every parameter.</summary>
  public IReadOnlyList<string> RequiredParameters { get; init; } =
      RequiredParameters ?? [.. Parameters.Select(p => p.Name)];

  /// <summary>True when the tool can change workspace files. A REFUSED call to such a tool
  ///     is a mutation that did not happen, so the exec bridge reports it instead of
  ///     letting the run read as a completed no-op (issue #375). Default false: a read, a
  ///     query, or a process spawn changes no file, so its refusal is not a lost write.</summary>
  public bool MutatesWorkspace { get; init; } = MutatesWorkspace;
}
