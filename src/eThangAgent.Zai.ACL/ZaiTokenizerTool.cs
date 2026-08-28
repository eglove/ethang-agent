using System.Globalization;
using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Zai.ACL;

/// <summary>Counts z.ai GLM model tokens for a piece of text through the tokenizer API.</summary>
public sealed class ZaiTokenizerTool(HttpClient http, ZaiConfiguration config) : ITool
{
  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly ZaiConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

  public ToolDefinition Definition { get; } = new(
      "count_tokens",
      "Count how many GLM tokens a piece of text uses (z.ai tokenizer). timeoutSeconds and text are " +
      "mandatory; text is non-empty and at most 400000 characters; model optionally selects the " +
      "tokenizer (one of glm-4.6, glm-4.6v, glm-4.5; default glm-4.6). Output is a single annotation " +
      "line `[count_tokens <model>: <total> token(s) total, <prompt> prompt token(s)]`. " +
      "Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("text", ToolParameterType.Text,
                "Non-empty text to count; at most 400000 characters."),
            new ToolParameter("model", ToolParameterType.Text,
                "Tokenizer model: glm-4.6, glm-4.6v, or glm-4.5. Default: glm-4.6."),
      ],
      ["timeoutSeconds", "text"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> envelope = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!envelope.IsSuccess)
    {
      return Task.FromResult(ZaiToolHttp.Err(envelope.Error!));
    }

    Result<ZaiTokenizerInput> parsed = ZaiTokenizerInput.Create(envelope.Value!.Arguments);
    return !parsed.IsSuccess
      ? Task.FromResult(ZaiToolHttp.Err(parsed.Error!))
      : ToolExecution.RunAsync(input.Name, envelope.Value.Timeout, token =>
        CountAsync(parsed.Value!, token), ct);
  }

  private async Task<ToolResult> CountAsync(ZaiTokenizerInput v, CancellationToken ct)
  {
    Result<JsonElement> response = await ZaiToolHttp.PostJsonAsync(
        _http, _config, ZaiToolHttp.TokenizerPath, new
        {
          model = v.Model,
          messages = new object[] { new { role = "user", content = v.Text } },
        }, ct).ConfigureAwait(false);
    if (!response.IsSuccess)
    {
      return ZaiToolHttp.Err(response.Error!);
    }

    if (!response.Value.TryGetProperty("usage", out JsonElement usage)
        || !usage.TryGetProperty("total_tokens", out JsonElement total)
        || total.ValueKind != JsonValueKind.Number)
    {
      return ZaiToolHttp.Err(new DomainError("ProviderError",
          "z.ai tokenizer response carried no usage.total_tokens."));
    }

    int prompt = usage.TryGetProperty("prompt_tokens", out JsonElement promptEl)
        && promptEl.ValueKind == JsonValueKind.Number
        ? promptEl.GetInt32()
        : total.GetInt32();
    return new ToolResult(string.Create(
        CultureInfo.InvariantCulture, $"[count_tokens {v.Model}: {total.GetInt32()} token(s) total, {prompt} prompt token(s)]"), false);
  }
}
