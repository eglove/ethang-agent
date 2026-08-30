using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire;

/// <summary>Wire-level core for OpenAI-compatible provider streams: classifies SSE
///     lines, applies chunks (content and reasoning deltas, tool-call fragment
///     accumulation, usage frames, finish reasons), and assembles the final
///     <see cref="ModelResponse"/>. Provider ACLs keep everything provider-specific —
///     endpoints, auth, request building, status mapping, JSON fallback parsing — and
///     supply only their <see cref="StreamVocabulary"/>. Shared deliberately: the
///     streaming logic is byte-identical across OpenAI-compatible providers and lives
///     OUTSIDE the domain, so the ACLs-share-no-domain-code doctrine (AGENTS.md) stays
///     intact. Error contract: JsonException → "Invalid provider stream",
///     InvalidOperationException → "Malformed provider stream"; structural
///     StreamedToolCall faults arrive as InvalidOperationException from ToRequest.</summary>
public static class OpenAiCompatStreamCore
{
  private const string ToolCallsField = "tool_calls";

  /// <summary>Consumes an SSE body: "data: {json}" frames carrying delta objects,
  ///     ":"-prefixed keep-alive comments, and the "data: [DONE]" terminator. Content
  ///     and reasoning fragments stream straight through; tool-call fragments accumulate
  ///     per index until the stream ends.</summary>
  public static async Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      StreamVocabulary vocabulary,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(response);
    ArgumentNullException.ThrowIfNull(vocabulary);
    StringBuilder content = new();
    Dictionary<int, StreamedToolCall> toolCalls = [];
    FinishReason finishReason = FinishReason.Stop;
    TokenUsage? usage = null;
    bool sawDone = false;
    try
    {
      using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      using StreamReader reader = new(stream);
      while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
      {
        string? payload = DataPayloadOf(line);
        if (payload is null)
        {
          continue; // separator, keep-alive comment, or non-data line
        }

        if (payload == "[DONE]")
        {
          sawDone = true;
          break;
        }

        using JsonDocument doc = JsonDocument.Parse(payload);
        if (ApplyChunk(doc.RootElement, vocabulary, content, toolCalls, onContentDelta, onReasoningDelta, out TokenUsage? frameUsage)
            is { } chunkReason)
        {
          finishReason = chunkReason;
        }

        if (frameUsage is not null)
        {
          usage = frameUsage; // last usage frame wins
        }
      }

      // A stream that ends without [DONE] was cut off (connection drop, proxy kill),
      // not completed. Failing loudly beats returning a silently truncated response;
      // non-retryable because deltas already streamed to the observer.
      if (!sawDone)
      {
        return Result.Failure<ModelResponse>(new DomainError("StreamInterrupted",
            "Provider stream ended without its [DONE] terminator."));
      }

      // Assembled inside the guard: strict fragment validation (missing id/name) is a
      // provider-stream failure delivered as a Result, never an escaped exception.
      return Result.Success(new ModelResponse(
          content.Length > 0 ? content.ToString() : null,
          [.. toolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToRequest())],
          finishReason,
          usage));
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError("ProviderError",
          $"Invalid provider stream: {ex.Message}"));
    }
    catch (InvalidOperationException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError("ProviderError",
          $"Malformed provider stream: {ex.Message}"));
    }
  }

  /// <summary>Classifies one SSE line: the trimmed payload of a "data:" frame, or
  ///     null for anything else (empty separators, ":"-prefixed keep-alive comments,
  ///     non-data lines). The "[DONE]" terminator flows through as its literal payload;
  ///     an empty data payload falls through to the parser and fails loudly, preserving
  ///     the strict stream contract.</summary>
  private static string? DataPayloadOf(string line)
    => line.Length == 0 || line.StartsWith(':') || !line.StartsWith("data:", StringComparison.Ordinal)
        ? null
        : line["data:".Length..].Trim();

  /// <summary>Applies one SSE chunk and returns the chunk's finish_reason when it
  ///     carries one, else null (delta/usage frames). Writes the chunk's usage object,
  ///     when it carries one, to <paramref name="usage"/> — the final usage frame wins.</summary>
  private static FinishReason? ApplyChunk(JsonElement chunk, StreamVocabulary vocabulary, StringBuilder content,
      Dictionary<int, StreamedToolCall> toolCalls,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      out TokenUsage? usage)
  {
    usage = ParseUsage(chunk);
    if (!chunk.TryGetProperty("choices", out JsonElement choices)
        || choices.ValueKind != JsonValueKind.Array
        || choices.GetArrayLength() == 0)
    {
      return null; // usage-only or heartbeat frames carry no choices
    }

    JsonElement choice = choices[0];
    if (!choice.TryGetProperty("delta", out JsonElement delta))
    {
      return null;
    }

    ApplyContentDelta(delta, content, onContentDelta);
    ApplyReasoningDeltas(delta, vocabulary.ReasoningFields, onReasoningDelta);
    ApplyToolCallFragments(delta, toolCalls);
    return ParseChunkFinishReason(choice, vocabulary.FinishReasons);
  }

  /// <summary>Streams one content fragment: appended to the assembled response and
  ///     forwarded to the observer. Structural no-op frames carry no information and
  ///     emit nothing.</summary>
  private static void ApplyContentDelta(JsonElement delta, StringBuilder content, Action<string>? onContentDelta)
  {
    if (delta.TryGetProperty("content", out JsonElement contentDelta)
        && contentDelta.ValueKind == JsonValueKind.String)
    {
      string text = contentDelta.GetString()!;
      if (text.Length > 0)
      {
        _ = content.Append(text);
        onContentDelta?.Invoke(text);
      }
    }
  }

  /// <summary>Streams reasoning fragments from the provider's reasoning fields, first
  ///     match wins.</summary>
  private static void ApplyReasoningDeltas(JsonElement delta, IReadOnlyList<string> fields, Action<string>? onReasoningDelta)
  {
    foreach (string field in fields)
    {
      if (delta.TryGetProperty(field, out JsonElement reasoning)
          && reasoning.ValueKind == JsonValueKind.String
          && reasoning.GetString() is { } text
          && text.Length > 0)
      {
        onReasoningDelta?.Invoke(text);
        return;
      }
    }
  }

  private static void ApplyToolCallFragments(JsonElement delta, Dictionary<int, StreamedToolCall> toolCalls)
  {
    if (delta.TryGetProperty(ToolCallsField, out JsonElement calls)
        && calls.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement call in calls.EnumerateArray())
      {
        ApplyToolCallFragment(call, toolCalls);
      }
    }
  }

  /// <summary>Assembles one tool-call fragment: addressed by index, created on first
  ///     sight, with id/name/argument text merged into it.</summary>
  private static void ApplyToolCallFragment(JsonElement call, Dictionary<int, StreamedToolCall> toolCalls)
  {
    int index = call.TryGetProperty("index", out JsonElement idx)
        && idx.ValueKind == JsonValueKind.Number
            ? idx.GetInt32()
            : toolCalls.Count;
    if (!toolCalls.TryGetValue(index, out StreamedToolCall? fragment))
    {
      toolCalls[index] = fragment = new StreamedToolCall();
    }

    if (call.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
    {
      fragment.Id = id.GetString();
    }

    ApplyFunctionFragment(call, fragment);
  }

  private static void ApplyFunctionFragment(JsonElement call, StreamedToolCall fragment)
  {
    if (!call.TryGetProperty("function", out JsonElement function))
    {
      return;
    }

    if (function.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
    {
      fragment.Name = name.GetString();
    }

    if (function.TryGetProperty("arguments", out JsonElement arguments)
        && arguments.ValueKind == JsonValueKind.String)
    {
      fragment.AppendArguments(arguments.GetString()!);
    }
  }

  /// <summary>Per-chunk translation of the provider's finish_reason vocabulary. Missing
  ///     on a delta frame → null, so it never overwrites an already-seen reason; an
  ///     unrecognized string maps to Unknown (the provider DID say, just not in a known
  ///     vocabulary — the same distinction the per-provider switches made).</summary>
  private static FinishReason? ParseChunkFinishReason(JsonElement choice, IReadOnlyDictionary<string, FinishReason> vocabulary)
    => choice.TryGetProperty("finish_reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String
        ? MapFinishReason(reason.GetString()!, vocabulary)
        : null;

  private static FinishReason MapFinishReason(string value, IReadOnlyDictionary<string, FinishReason> vocabulary)
    => vocabulary.TryGetValue(value, out FinishReason mapped) ? mapped : FinishReason.Unknown;

  /// <summary>Maps the OpenAI-compatible usage object (prompt_tokens / completion_tokens /
  ///     prompt_tokens_details.cached_tokens) into TokenUsage; null when absent.</summary>
  private static TokenUsage? ParseUsage(JsonElement parent)
  {
    if (!parent.TryGetProperty("usage", out JsonElement u) || u.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    if (!TryGetInt(u, "prompt_tokens", out int prompt) || !TryGetInt(u, "completion_tokens", out int completion))
    {
      return null;
    }

    int? cached = null;
    if (u.TryGetProperty("prompt_tokens_details", out JsonElement details)
        && details.ValueKind == JsonValueKind.Object
        && TryGetInt(details, "cached_tokens", out int cachedValue))
    {
      cached = cachedValue;
    }

    return new TokenUsage(prompt, completion, cached);
  }

  private static bool TryGetInt(JsonElement parent, string name, out int value)
  {
    value = 0;
    return parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
  }

  /// <summary>Accumulates one streamed tool call: id/name arrive on the first fragment,
  ///     argument text concatenates across every fragment for that index.</summary>
  private sealed class StreamedToolCall
  {
    public string? Id { get; set; }
    public string? Name { get; set; }

    private readonly StringBuilder _arguments = new();

    public void AppendArguments(string fragment) => _arguments.Append(fragment);

    public ToolCallRequest ToRequest() => new(
        Id ?? throw new InvalidOperationException("Streamed tool call carried no id."),
        Name ?? throw new InvalidOperationException(
            $"Streamed tool call '{Id}' carried no function name."),
        _arguments.ToString());
  }
}
