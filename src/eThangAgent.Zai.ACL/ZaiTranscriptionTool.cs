using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Transcribes a short workspace audio clip (.wav/.mp3, ≤25MB, ≤30s of audio)
///     with GLM-ASR. Optional context carries prior transcription text for continuity.</summary>
public sealed class ZaiTranscriptionTool(
    HttpClient http, ZaiConfiguration config, IPathResolver resolver, IFileSystemAccess files) : ITool
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "transcribe_audio",
      "Transcribe one short audio clip with z.ai GLM-ASR. timeoutSeconds and path are mandatory; " +
      "path is workspace-relative and must be .wav or .mp3 (≤ 25MB and ≤ 30 seconds of audio — " +
      "longer audio must be split first); context optionally supplies prior transcription text " +
      "(≤ 8000 characters) for continuity across clips. Output begins with an annotation line " +
      "`[transcribed <path>]` followed by the transcript text. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text, "Workspace-relative .wav or .mp3 audio path."),
            new ToolParameter("context", ToolParameterType.Text,
                "Prior transcription text for continuity; at most 8000 characters."),
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

    Result<ZaiTranscriptionInput> parsed = ZaiTranscriptionInput.Create(envelope.Value!.Arguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(parsed.Error!));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value!.Path);
    return !resolved.IsSuccess
      ? Task.FromResult(ZaiToolHttp.Err(resolved.Error!))
      : ToolExecution.RunAsync(input.Name, envelope.Value.Timeout, token =>
        TranscribeAsync(parsed.Value, resolved.Value!, token), ct);
  }

  private async Task<ToolResult> TranscribeAsync(
      ZaiTranscriptionInput v, string resolvedPath, CancellationToken ct)
  {
    Result<byte[]> bytes = await _files.ReadBytesAsync(resolvedPath, ct).ConfigureAwait(false);
    if (!bytes.IsSuccess)
    {
      return ZaiToolHttp.Err(bytes.Error!);
    }
    if (bytes.Value!.Length > ZaiTranscriptionInput.ByteLimit)
    {
      return ZaiToolHttp.Err(new DomainError("InvalidParameterValue",
          $"'{v.Path}' is {bytes.Value.Length} bytes; the limit is {ZaiTranscriptionInput.ByteLimit}."));
    }

    // MultipartFormDataContent OWNS and disposes every part added to it, so the parts
    // are intentionally not `using` locals — a block-scoped using would dispose them
    // before the request is sent (observed as a disposed StringContent mid-send).
#pragma warning disable CA2000 // Dispose objects before losing scope — form owns its parts
    MultipartFormDataContent form = [];
    ByteArrayContent fileContent = new(bytes.Value);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    form.Add(fileContent, "file", Path.GetFileName(resolvedPath));
    form.Add(new StringContent("glm-asr-2512"), "model");
    if (!string.IsNullOrEmpty(v.Context))
    {
      form.Add(new StringContent(v.Context), "prompt");
    }
#pragma warning restore CA2000

    JsonElement root;
    try
    {
      using HttpRequestMessage request = new(HttpMethod.Post,
          _config.Endpoint(ZaiToolHttp.TranscriptionsPath))
      { Content = form };
      request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
      using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return ZaiToolHttp.Err(new DomainError("ProviderError",
            $"z.ai returned HTTP {(int)response.StatusCode}: {Truncate(body, 200)}"));
      }
      root = JsonDocument.Parse(body).RootElement;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
    {
      return ZaiToolHttp.Err(new DomainError("ProviderError", ex.Message));
    }

    return !root.TryGetProperty("text", out JsonElement text)
        || text.ValueKind != JsonValueKind.String
        || text.GetString()!.Length == 0
      ? ZaiToolHttp.Err(new DomainError("ProviderError",
          "z.ai transcription response carried no text."))
      : new ToolResult(string.Create(CultureInfo.InvariantCulture,
        $"[transcribed {v.Path}]\n{text.GetString()}"), false);
  }

  private static string Truncate(string text, int limit)
      => text.Length <= limit ? text : text[..limit] + "…";
}
