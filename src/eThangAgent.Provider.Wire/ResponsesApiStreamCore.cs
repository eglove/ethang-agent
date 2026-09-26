using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire;

/// <summary>Wire-level core for Responses-API provider streams: classifies SSE lines,
///     applies typed events (content and reasoning deltas, function-call argument
///     fragments, the terminal response carrying usage and status), and assembles the
///     final <see cref="ModelResponse"/>. Shared deliberately outside the domain —
///     same standing as <see cref="ResponsesApiRequestCore"/>.
///
///     Event tolerance: providers frame the stream either with an "event:" line
///     ("event: response.output_text.delta") or with the type inside the JSON payload
///     ("data: {&quot;type&quot;:"response.output_text.delta",...}" — OpenRouter's live
///     framing, verified 2026-09). The payload's "type" field is authoritative, the
///     event line the fallback. Both the canonical OpenAI vocabulary
///     (response.output_text.delta / response.reasoning_*.delta /
///     response.function_call_arguments.delta / response.completed|incomplete) and
///     OpenRouter's documented content_part variants (response.content_part.delta,
///     response.done) are recognized. Error contract mirrors the request core:
///     JsonException → "Invalid provider stream", InvalidOperationException →
///     "Malformed provider stream".</summary>
public static class ResponsesApiStreamCore
{
  /// <summary>Event types carrying a text fragment in their "delta" field, with the
  ///     sink it feeds: content, or reasoning. response.content_part.delta is decided
  ///     per-frame by its part type; response.reasoning_summary_text.delta is the
  ///     summarized-reasoning variant some models emit.</summary>
  private static readonly Dictionary<string, DeltaSink> DeltaEvents = new()
  {
    ["response.output_text.delta"] = DeltaSink.Content,
    ["response.reasoning_text.delta"] = DeltaSink.Reasoning,
    ["response.reasoning_summary_text.delta"] = DeltaSink.Reasoning,
  };

  private const string ContentPartDeltaEvent = "response.content_part.delta";

  public static async Task<Result<ModelResponse>> ReadSseStreamAsync(HttpResponseMessage response,
      Action<string>? onContentDelta,
      Action<string>? onReasoningDelta,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(response);
    StringBuilder content = new();
    Dictionary<string, StreamedToolCall> toolCalls = []; // keyed by output_index
    List<ServerToolCall> serverToolCalls = [];
    FinishReason finishReason = FinishReason.Stop;
    TokenUsage? usage = null;
    bool sawDone = false;
    try
    {
      using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      using StreamReader reader = new(stream);
      while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
      {
        if (line.Length == 0 || line.StartsWith(':'))
        {
          continue; // separator or keep-alive comment
        }

        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
          continue; // the payload's "type" field is authoritative; event line is fallback
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
          continue;
        }

        string payload = line["data:".Length..].Trim();
        if (payload == "[DONE]")
        {
          sawDone = true;
          break;
        }

        if (payload.Length == 0)
        {
          continue; // empty data payload falls through to the strict [DONE] contract
        }

        using JsonDocument doc = JsonDocument.Parse(payload);
        ApplyEvent(doc.RootElement, content, toolCalls, serverToolCalls, onContentDelta, onReasoningDelta,
            ref finishReason, ref usage);
      }

      // A stream that ends without [DONE] was cut off (connection drop, proxy kill),
      // not completed. Failing loudly beats returning a silently truncated response;
      // non-retryable because deltas already streamed to the observer.
      string? assembled = content.Length > 0 ? content.ToString() : null;
      if (toolCalls.Count > 0)
      {
        // Assembled function calls imply ToolCalls regardless of what (or whether)
        // the terminal event said — the same contract the non-streaming parser
        // applies, so a stream missing its terminal frame still dispatches the tool
        // loop.
        finishReason = FinishReason.ToolCalls;
      }

      Result<ModelResponse> result = !sawDone
        ? Result.Failure<ModelResponse>(new DomainError("StreamInterrupted",
            "Provider stream ended without its [DONE] terminator."))
        : Result.Success(new ModelResponse(
          assembled,
          [.. toolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToRequest())],
          finishReason,
          usage,
          serverToolCalls));
      return result;
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

  private enum DeltaSink
  {
    Content,
    Reasoning,
  }

  /// <summary>Applies one typed event. Structural faults (malformed fragments) surface
  ///     as InvalidOperationException → the "Malformed provider stream" failure.</summary>
  private static void ApplyEvent(JsonElement evt, StringBuilder content,
      Dictionary<string, StreamedToolCall> toolCalls, List<ServerToolCall> serverToolCalls,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta,
      ref FinishReason finishReason, ref TokenUsage? usage)
  {
    string type = evt.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
        ? t.GetString()! : "";

    switch (type)
    {
      case "response.output_item.added":
        ApplyItemAdded(evt, toolCalls, serverToolCalls);
        return;

      case "response.function_call_arguments.delta":
        ApplyArgumentsDelta(evt, toolCalls);
        return;

      case "response.function_call_arguments.done":
        // The final arguments string, authoritative over the accumulated deltas.
        ApplyArgumentsDone(evt, toolCalls);
        return;

      case "response.completed":
      case "response.incomplete":
      case "response.done":
        ApplyTerminal(evt, toolCalls, ref finishReason, ref usage);
        return;

      default:
        if (DeltaEvents.TryGetValue(type, out DeltaSink sink))
        {
          ApplyDelta(evt, sink, content, onContentDelta, onReasoningDelta);
        }
        else if (type == ContentPartDeltaEvent)
        {
          // OpenRouter's documented variant: the part type decides the sink.
          ApplyDelta(evt, PartSinkOf(evt), content, onContentDelta, onReasoningDelta);
        }

        return;
    }
  }

  private static DeltaSink PartSinkOf(JsonElement evt)
    => evt.TryGetProperty("part", out JsonElement part)
        && part.ValueKind == JsonValueKind.Object
        && part.TryGetProperty("type", out JsonElement partType)
        && partType.ValueKind == JsonValueKind.String
        && partType.GetString()!.StartsWith("reasoning", StringComparison.Ordinal)
        ? DeltaSink.Reasoning
        : DeltaSink.Content;

  /// <summary>A new output item: function_call items register their call_id and name —
  ///     both arrive complete on the added event; arguments stream after.</summary>
  private static void ApplyItemAdded(JsonElement evt, Dictionary<string, StreamedToolCall> toolCalls,
      List<ServerToolCall> serverToolCalls)
  {
    if (!evt.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object
        || !item.TryGetProperty("type", out JsonElement itemType)
        || itemType.ValueKind != JsonValueKind.String)
    {
      return;
    }

    // Server-tool call items (web_search_call etc.) register as surfaced calls —
    // they execute server-side and never enter the message history.
    string addedType = itemType.GetString()!;
    if (addedType != "function_call")
    {
      if (ServerToolCallItem.TryParse(addedType, item, out ServerToolCall? serverCall))
      {
        serverToolCalls.Add(serverCall);
      }

      return;
    }

    string key = OutputIndexKey(evt);
    if (!toolCalls.TryGetValue(key, out StreamedToolCall? fragment))
    {
      toolCalls[key] = fragment = new StreamedToolCall();
    }

    fragment.Id = item.TryGetProperty("call_id", out JsonElement callId) && callId.ValueKind == JsonValueKind.String
        ? callId.GetString()
        : null;
    if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
    {
      fragment.Name = name.GetString();
    }
  }

  private static void ApplyArgumentsDelta(JsonElement evt, Dictionary<string, StreamedToolCall> toolCalls)
  {
    if (!toolCalls.TryGetValue(OutputIndexKey(evt), out StreamedToolCall? fragment))
    {
      throw new InvalidOperationException(
          "Function-call arguments delta arrived before its output_item.added event.");
    }

    if (evt.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.String)
    {
      fragment.AppendArguments(delta.GetString()!);
    }
  }

  private static void ApplyArgumentsDone(JsonElement evt, Dictionary<string, StreamedToolCall> toolCalls)
  {
    if (!toolCalls.TryGetValue(OutputIndexKey(evt), out StreamedToolCall? fragment))
    {
      throw new InvalidOperationException(
          "Function-call arguments done arrived before its output_item.added event.");
    }

    if (evt.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.String)
    {
      fragment.ReplaceArguments(args.GetString()!);
    }
  }

  /// <summary>The terminal event carries the final response object: usage rides it;
  ///     its status/incomplete_details decide the finish reason — with assembled
  ///     function calls outranking any status, matching the non-streaming parser.</summary>
  private static void ApplyTerminal(JsonElement evt, Dictionary<string, StreamedToolCall> toolCalls,
      ref FinishReason finishReason, ref TokenUsage? usage)
  {
    if (evt.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object)
    {
      usage = ResponsesApiRequestCore.ParseUsage(response);
      finishReason = ParseTerminalFinishReason(response, toolCalls.Count > 0);
    }
  }

  private static FinishReason ParseTerminalFinishReason(JsonElement response, bool hasToolCalls)
  {
    if (hasToolCalls)
    {
      return FinishReason.ToolCalls;
    }

    if (!response.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.String)
    {
      return FinishReason.Stop;
    }

    FinishReason reason = FinishReason.Unknown;
    switch (status.GetString())
    {
      case "completed":
        reason = FinishReason.Stop;
        break;
      case "incomplete":
        reason = IncompleteReason(response);
        break;
      default:
        break;
    }

    return reason;
  }

  private static FinishReason IncompleteReason(JsonElement response)
  {
    FinishReason reason = FinishReason.Unknown;
    if (response.TryGetProperty("incomplete_details", out JsonElement details)
        && details.ValueKind == JsonValueKind.Object
        && details.TryGetProperty("reason", out JsonElement reasonEl)
        && reasonEl.ValueKind == JsonValueKind.String)
    {
      switch (reasonEl.GetString())
      {
        case "max_output_tokens":
          reason = FinishReason.Length;
          break;
        case "content_filter":
          reason = FinishReason.ContentFilter;
          break;
        default:
          break;
      }
    }

    return reason;
  }

  private static void ApplyDelta(JsonElement evt, DeltaSink sink, StringBuilder content,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta)
  {
    if (!evt.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.String)
    {
      return;
    }

    string text = delta.GetString()!;
    if (text.Length == 0)
    {
      return;
    }

    if (sink == DeltaSink.Content)
    {
      _ = content.Append(text);
      onContentDelta?.Invoke(text);
    }
    else
    {
      onReasoningDelta?.Invoke(text);
    }
  }

  private static string OutputIndexKey(JsonElement evt)
    => evt.TryGetProperty("output_index", out JsonElement idx) && idx.ValueKind == JsonValueKind.Number
        && idx.TryGetInt32(out int index)
            ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Event carried no output_index.");

  /// <summary>Accumulates one streamed tool call: call_id/name arrive on the added
  ///     event, argument text concatenates across delta events and is replaced by the
  ///     authoritative done event.</summary>
  private sealed class StreamedToolCall
  {
    public string? Id { get; set; }
    public string? Name { get; set; }

    private readonly StringBuilder _arguments = new();

    public void AppendArguments(string fragment) => _arguments.Append(fragment);

    public void ReplaceArguments(string arguments)
    {
      _ = _arguments.Clear();
      _ = _arguments.Append(arguments);
    }

    public ToolCallRequest ToRequest() => new(
        Id ?? throw new InvalidOperationException("Streamed tool call carried no call_id."),
        Name ?? throw new InvalidOperationException(
            $"Streamed tool call '{Id}' carried no function name."),
        _arguments.ToString());
  }
}
