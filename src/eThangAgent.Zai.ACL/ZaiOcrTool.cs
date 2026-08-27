using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Recognizes a workspace document (PDF or image) with GLM-OCR and returns the
///     markdown transcription. Local limits mirror the API's: ≤10MB per image, ≤50MB per
///     PDF, at most 30 pages.</summary>
public sealed class ZaiOcrTool(
    HttpClient http, ZaiConfiguration config, IPathResolver resolver, IFileSystemAccess files) : ITool
{
  /// <summary>Bounded output; longer transcriptions are cut with a visible marker.</summary>
  internal const int ResultLimit = 50_000;

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "ocr_document",
      "Transcribe a workspace document (PDF, JPG, or PNG) to markdown with z.ai GLM-OCR. " +
      "timeoutSeconds and path are mandatory; path is workspace-relative (.pdf ≤ 50MB and ≤ 30 pages, " +
      ".jpg/.png ≤ 10MB); startPage/endPage optionally bound PDF parsing (integers ≥ 1, end ≥ start). " +
      "Output begins with an annotation line `[ocr <path>: <num_pages> page(s)]` followed by the " +
      "markdown transcription (above 50000 characters, cut with a final line " +
      "`[truncated at 50000 of <total> characters]`). Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "Workspace-relative document path: .pdf, .jpg, or .png."),
            new ToolParameter("startPage", ToolParameterType.WholeNumber,
                "First PDF page to parse (1-based). Minimum: 1", Minimum: 1),
            new ToolParameter("endPage", ToolParameterType.WholeNumber,
                "Last PDF page to parse. Minimum: 1", Minimum: 1),
      ],
      ["timeoutSeconds", "path"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> envelope = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!envelope.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(envelope.Error!));
    }

    Result<ZaiOcrInput> parsed = ZaiOcrInput.Create(envelope.Value!.Arguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(parsed.Error!));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value!.Path);
    return !resolved.IsSuccess
      ? Task.FromResult(ZaiToolHttp.Err(resolved.Error!))
      : ToolExecution.RunAsync(input.Name, envelope.Value.Timeout, token =>
        OcrAsync(parsed.Value, resolved.Value!, token), ct);
  }

  private async Task<ToolResult> OcrAsync(ZaiOcrInput v, string resolvedPath, CancellationToken ct)
  {
    Result<byte[]> bytes = await _files.ReadBytesAsync(resolvedPath, ct).ConfigureAwait(false);
    if (!bytes.IsSuccess)
    {
      return ZaiToolHttp.Err(bytes.Error!);
    }

    bool isPdf = resolvedPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    long byteLimit = isPdf ? ZaiOcrInput.PdfByteLimit : ZaiOcrInput.ImageByteLimit;
    if (bytes.Value!.Length > byteLimit)
    {
      return ZaiToolHttp.Err(new DomainError("InvalidParameterValue",
          $"'{v.Path}' is {bytes.Value.Length} bytes; the {(isPdf ? "PDF" : "image")} limit is {byteLimit}."));
    }

    Dictionary<string, object?> body = new()
    {
      ["model"] = "glm-ocr",
      ["file"] = Convert.ToBase64String(bytes.Value!),
    };
    if (v.StartPage is { } start)
    {
      body["start_page_id"] = start;
    }
    if (v.EndPage is { } end)
    {
      body["end_page_id"] = end;
    }

    Result<JsonElement> response = await ZaiToolHttp.PostJsonAsync(
        _http, _config, ZaiToolHttp.LayoutParsingPath, body, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return ZaiToolHttp.Err(response.Error!);
    }

    if (!response.Value!.TryGetProperty("md_results", out JsonElement md)
        || md.ValueKind != JsonValueKind.String
        || md.GetString()!.Length == 0)
    {
      return ZaiToolHttp.Err(new DomainError("ProviderError",
          "z.ai OCR response carried no md_results transcription."));
    }

    string result = md.GetString()!;
    int pages = response.Value.TryGetProperty("data_info", out JsonElement dataInfo)
        && dataInfo.TryGetProperty("num_pages", out JsonElement pageCount)
        && pageCount.ValueKind == JsonValueKind.Number
        ? pageCount.GetInt32()
        : (v.EndPage - v.StartPage + 1) ?? 1;

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[ocr {v.Path}: {pages} page(s)]\n");
    _ = result.Length <= ResultLimit
      ? sb.Append(result)
      : sb.Append(result[..ResultLimit])
          .Append(CultureInfo.InvariantCulture, $"\n[truncated at {ResultLimit} of {result.Length} characters]");
    return new ToolResult(sb.ToString(), false);
  }
}
