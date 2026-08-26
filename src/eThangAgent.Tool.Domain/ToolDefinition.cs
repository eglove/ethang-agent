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
    IReadOnlyList<string>? RequiredParameters = null)
{
  /// <summary>Parameter names the caller must supply; defaults to every parameter.</summary>
  public IReadOnlyList<string> RequiredParameters { get; init; } =
      RequiredParameters ?? [.. Parameters.Select(p => p.Name)];
}
