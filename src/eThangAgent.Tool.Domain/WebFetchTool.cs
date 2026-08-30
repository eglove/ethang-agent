using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Fetches a web resource and renders it for the model: HTML bodies go
///     through an <see cref="IHtmlToMarkdown" /> converter; other textual bodies are
///     passed through verbatim; non-textual bodies are a typed error. Output begins
///     with an annotation line documenting the FINAL url after redirects, the status,
///     content type, and body size, so the model always knows what it is looking at.</summary>
public sealed class WebFetchTool(IWebAccess web, IHtmlToMarkdown converter) : ITool
{
  private const string ToolName = "web_fetch";
  private const string UrlName = "url";
  private const string VerbatimTag = ", verbatim";

  public ToolDefinition Definition { get; } = new(
      ToolName,
      "Fetch a web page or resource over HTTP(S) and return it as readable text. " +
      "HTML pages are converted to markdown; other textual responses (plain text, JSON, " +
      "XML, and so on) are returned verbatim. Binary responses are rejected. The FIRST " +
      "line is always an annotation: [web-fetch <final-url> — <status> <reason>, " +
      "<content-type>, <size> → <size> markdown] for converted pages or " +
      "[web-fetch <final-url> — <status> <reason>, <content-type>, <size>, verbatim] for " +
      "everything else. Redirects are followed; the annotation reports where the fetch " +
      "actually landed, which may differ from the requested url. Errors begin with " +
      "Error [Code]:.",
      [
          new ToolParameter(UrlName, ToolParameterType.Text,
              "Absolute http/https URL to fetch. Other schemes (file, ftp, javascript, " +
              "data, and so on) are rejected."),
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
      ],
      [UrlName, ToolTimeout.ParameterName]);

  public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<WebFetchInput> args = WebFetchInput.Create(input.JsonArguments);
    if (!args.IsSuccess)
    {
      return Err(args.Error);
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Err(budget.Error)
      : await ToolExecution.RunAsync(input.Name, budget.Value.Timeout, async token =>
          await FetchAndRenderAsync(args.Value.Url, token).ConfigureAwait(false), ct).ConfigureAwait(false);
  }

  private async Task<ToolResult> FetchAndRenderAsync(Uri url, CancellationToken ct)
  {
    Result<WebResource> fetched = await web.FetchAsync(url, ct).ConfigureAwait(false);
    if (!fetched.IsSuccess)
    {
      return Err(fetched.Error);
    }

    WebResource resource = fetched.Value;
    string annotation = $"[web-fetch {resource.Url} — {resource.StatusCode} {resource.ReasonPhrase}, " +
        $"{resource.ContentType}, ";
    if (IsHtml(resource.ContentType))
    {
      string markdown = converter.Convert(resource.Body, resource.Url);
      return new ToolResult(
          annotation + $"{Kb(resource.ByteCount)} → {Kb(System.Text.Encoding.UTF8.GetByteCount(markdown))} markdown]" +
          "\n" + markdown,
          false);
    }

    return IsTextual(resource.ContentType)
      ? new ToolResult(
          annotation + $"{Kb(resource.ByteCount)}{VerbatimTag}]" +
          "\n" + resource.Body,
          false)
      : new ToolResult($"Error [UnsupportedMediaType]: '{resource.Url}' responded with " +
          $"'{resource.ContentType}' (HTTP {resource.StatusCode} {resource.ReasonPhrase}). Only " +
          "textual content is returned; binary bodies are rejected.", true);

  }

  /// <summary>HTML when the content type says html — parameters and vendored types
  ///     included.</summary>
  private static bool IsHtml(string contentType) =>
      contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

  /// <summary>Textual when the media type is text/* or a common textual application
  ///     type (json, xml, javascript, yaml). Anything else is treated as binary.</summary>
  private static bool IsTextual(string contentType)
  {
    string media = contentType.Split(';')[0].Trim().ToUpperInvariant();
    return media.StartsWith("TEXT/", StringComparison.Ordinal)
        || media.EndsWith("+XML", StringComparison.Ordinal)
        || media is "APPLICATION/JSON" or "APPLICATION/XML" or "APPLICATION/JAVASCRIPT"
            or "APPLICATION/YAML" or "APPLICATION/X-YAML";
  }

  /// <summary>Human-readable size: bytes under 1024, one-decimal KB under 1 MB, MB beyond.</summary>
  private static string Kb(long bytes) => bytes switch
  {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{(double)bytes / 1024:0.#} KB",
    _ => $"{(double)bytes / (1024 * 1024):0.#} MB",
  };

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
