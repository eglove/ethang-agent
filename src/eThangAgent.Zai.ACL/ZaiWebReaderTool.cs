using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Fetches and parses a web page into markdown through z.ai's reader API.</summary>
public sealed class ZaiWebReaderTool(HttpClient http, ZaiConfiguration config) : ITool
{
  /// <summary>Bounded output: pages longer than this are cut with a visible marker —
  ///     the model re-reads narrower or follows links itself when it needs more.</summary>
  internal const int ContentLimit = 30_000;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

  public ToolDefinition Definition { get; } = new(
      "web_read",
      "Fetch one web page and return its main content as markdown (z.ai reader). timeoutSeconds and " +
      "url are mandatory; url must be absolute http(s). Output begins with an annotation line " +
      "`[web_read '<title>' from <url>]` followed by the page content. Content above 30000 characters " +
      "is cut with a final line `[truncated at 30000 of <total> characters]` — the model should narrow " +
      "its need rather than expect more. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("url", ToolParameterType.Text, "Absolute http(s) URL of the page to read."),
      ],
      ["timeoutSeconds", "url"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> envelope = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!envelope.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(envelope.Error!));
    }

    Result<ZaiWebReaderInput> parsed = ZaiWebReaderInput.Create(envelope.Value!.Arguments);
    return !parsed.IsSuccess
      ? Task.FromResult(ZaiToolHttp.Err(parsed.Error!))
      : ToolExecution.RunAsync(input.Name, envelope.Value.Timeout, token =>
        ReadAsync(parsed.Value!, token), ct);
  }

  private async Task<ToolResult> ReadAsync(ZaiWebReaderInput v, CancellationToken ct)
  {
    Result<JsonElement> response = await ZaiToolHttp.PostJsonAsync(
        _http, _config, ZaiToolHttp.WebReaderPath, new
        {
          url = v.Url,
          return_format = "markdown",
          retain_images = false,
        }, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return ZaiToolHttp.Err(response.Error!);
    }

    if (!response.Value.TryGetProperty("reader_result", out JsonElement result)
        || !result.TryGetProperty("content", out JsonElement contentEl)
        || contentEl.ValueKind != JsonValueKind.String
        || string.IsNullOrEmpty(contentEl.GetString()))
    {
      return ZaiToolHttp.Err(new DomainError("ProviderError",
          "z.ai reader response carried no readable content."));
    }

    string content = contentEl.GetString()!;
    string title = result.TryGetProperty("title", out JsonElement titleEl)
        && titleEl.ValueKind == JsonValueKind.String
        ? titleEl.GetString()!
        : v.Url.ToString();

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[web_read '{title}' from {v.Url}]\n");
    _ = content.Length <= ContentLimit
      ? sb.Append(content)
      : sb.Append(content[..ContentLimit])
          .Append(CultureInfo.InvariantCulture, $"\n[truncated at {ContentLimit} of {content.Length} characters]");
    return new ToolResult(sb.ToString(), false);
  }
}
