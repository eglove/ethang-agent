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
    StreamState state = new()
    {
      OnContentDelta = onContentDelta,
      OnReasoningDelta = onReasoningDelta,
    };
    bool sawDone = false;
    try
    {
      using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      using StreamReader reader = new(stream);
      while (true)
      {
        (string? payload, bool done) = await ReadNextDataPayloadAsync(reader, ct).ConfigureAwait(false);
        if (done)
        {
          sawDone = true;
          break;
        }

        if (payload is null)
        {
          break; // stream ended — handled below as an interruption
        }

        using JsonDocument doc = JsonDocument.Parse(payload);
        ApplyEvent(doc.RootElement, state);
      }

      // A stream that ends without [DONE] was cut off (connection drop, proxy kill),
      // not completed. Failing loudly beats returning a silently truncated response;
      // non-retryable because deltas already streamed to the observer.
      return sawDone
        ? Result.Success(state.ToResponse())
        : Result.Failure<ModelResponse>(new DomainError("StreamInterrupted",
            "Provider stream ended without its [DONE] terminator."));
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

  /// <summary>Reads the next data payload from the SSE stream: the [DONE] sentinel as
  ///     <c>done</c>, the next JSON payload as <c>payload</c>, or a null payload at
  ///     end-of-stream. Separator/keep-alive lines, "event:" lines (the payload's
  ///     "type" field is authoritative), non-data lines, and empty data payloads are
  ///     skipped; an empty data payload falls through to the strict [DONE] contract.</summary>
  private static async Task<(string? Payload, bool Done)> ReadNextDataPayloadAsync(
      StreamReader reader, CancellationToken ct)
  {
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
        return (null, true);
      }

      if (payload.Length > 0)
      {
        return (payload, false);
      }

      // empty data payload falls through to the strict [DONE] contract
    }

    return (null, false);
  }

  /// <summary>Applies one typed event. Structural faults (malformed fragments) surface
  ///     as InvalidOperationException → the "Malformed provider stream" failure.</summary>
  private static void ApplyEvent(JsonElement evt, StreamState state)
  {
    string type = EventTypeOf(evt);

    switch (type)
    {
      case "response.output_item.added":
        ApplyItemAdded(evt, state);
        return;

      case "response.output_item.done":
        // The done event carries the item's final action (query/url/sources) —
        // OpenRouter's added event for its "openrouter:<tool>" items carries
        // none. Done finalizes the call; a done for an unregistered item still
        // surfaces (tolerance, same contract as unknown item types).
        ApplyItemDone(evt, state);
        return;

      case "response.function_call_arguments.delta":
        ApplyArgumentsDelta(evt, state);
        return;

      case "response.function_call_arguments.done":
        // The final arguments string, authoritative over the accumulated deltas.
        ApplyArgumentsDone(evt, state);
        return;

      case "response.completed":
      case "response.incomplete":
      case "response.done":
        ApplyTerminal(evt, state);
        return;

      default:
        ApplyDefaultEvent(evt, type, state);
        return;
    }
  }

  /// <summary>Events outside the canonical vocabulary: the recognized delta types feed
  ///     their sink, everything else is ignored (the tolerance contract).</summary>
  private static void ApplyDefaultEvent(JsonElement evt, string type, StreamState state)
  {
    if (DeltaEvents.TryGetValue(type, out DeltaSink sink))
    {
      ApplyDelta(evt, sink, state);
      return;
    }

    if (type == ContentPartDeltaEvent)
    {
      // OpenRouter's documented variant: the part type decides the sink.
      ApplyDelta(evt, PartSinkOf(evt), state);
    }
  }

  private static string EventTypeOf(JsonElement evt)
    => evt.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
        ? t.GetString()! : "";

  private static DeltaSink PartSinkOf(JsonElement evt)
    => evt.TryGetProperty("part", out JsonElement part)
        && part.ValueKind == JsonValueKind.Object
        && part.TryGetProperty("type", out JsonElement partType)
        && partType.ValueKind == JsonValueKind.String
        && partType.GetString()!.StartsWith("reasoning", StringComparison.Ordinal)
        ? DeltaSink.Reasoning
        : DeltaSink.Content;

  /// <summary>A new output item: function_call items register their call_id and name —
  ///     both arrive complete on the added event; arguments stream after. Server-tool
  ///     items (web_search_call, openrouter:web_search, ...) register as surfaced
  ///     calls keyed by output index — they execute server-side and never enter the
  ///     message history.</summary>
  private static void ApplyItemAdded(JsonElement evt, StreamState state)
  {
    if (!TryGetTypedItem(evt, out JsonElement item, out string addedType))
    {
      return;
    }

    if (addedType != "function_call")
    {
      // Server-tool call items register as surfaced calls — they execute server-side
      // and never enter the message history. The added event may carry no action yet
      // (OpenRouter's openrouter:<tool> items do not); done finalizes the detail.
      RegisterServerToolCall(evt, addedType, item, state);
      return;
    }

    RegisterFunctionCall(evt, item, state);
  }

  /// <summary>The event's "item" object with its string "type": false when either
  ///     member is absent or ill-typed (the tolerance contract).</summary>
  private static bool TryGetTypedItem(JsonElement evt, out JsonElement item, out string itemType)
  {
    if (evt.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("type", out JsonElement typeEl)
        && typeEl.ValueKind == JsonValueKind.String)
    {
      itemType = typeEl.GetString()!;
      return true;
    }

    itemType = "";
    return false;
  }

  /// <summary>Registers a server-tool item as a surfaced call keyed by output index;
  ///     unknown item types parse as false and are ignored (the tolerance contract).</summary>
  private static void RegisterServerToolCall(JsonElement evt, string itemType, JsonElement item,
      StreamState state)
  {
    if (ServerToolCallItem.TryParse(itemType, item, out ServerToolCall? serverCall))
    {
      state.ServerToolCalls[OutputIndexKeyAsInt(evt)] = serverCall;
    }
  }

  /// <summary>Registers (or re-anchors) the streamed function call under the event's
  ///     output index and applies the added event's complete call_id and name —
  ///     arguments stream after.</summary>
  private static void RegisterFunctionCall(JsonElement evt, JsonElement item, StreamState state)
  {
    string key = OutputIndexKey(evt);
    if (!state.ToolCalls.TryGetValue(key, out StreamedToolCall? fragment))
    {
      state.ToolCalls[key] = fragment = new StreamedToolCall();
    }

    fragment.Id = item.TryGetProperty("call_id", out JsonElement callId) && callId.ValueKind == JsonValueKind.String
        ? callId.GetString()
        : null;
    if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
    {
      fragment.Name = name.GetString();
    }
  }

  /// <summary>An output item's final form. For a server-tool item this carries the
  ///     completed action (query/url/sources) the added event may have lacked: the
  ///     registered call is replaced. A done for an item never added still surfaces —
  ///     tolerance, the same contract the added event applies to unknown types.</summary>
  private static void ApplyItemDone(JsonElement evt, StreamState state)
  {
    if (!evt.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object
        || !item.TryGetProperty("type", out JsonElement itemType)
        || itemType.ValueKind != JsonValueKind.String)
    {
      return;
    }

    string doneType = itemType.GetString()!;
    if (doneType == "function_call" || !ServerToolCallItem.TryParse(doneType, item, out ServerToolCall? serverCall))
    {
      return;
    }

    state.ServerToolCalls[OutputIndexKeyAsInt(evt)] = serverCall;
  }

  /// <summary>The event's output_index as an int; an absent or non-numeric index
  ///     degrades to -1 (a single unindexed call still assembles, ordered first).</summary>
  private static int OutputIndexKeyAsInt(JsonElement evt)
  {
    return evt.TryGetProperty("output_index", out JsonElement index) && index.ValueKind == JsonValueKind.Number
        ? index.GetInt32()
        : -1;
  }

  private static void ApplyArgumentsDelta(JsonElement evt, StreamState state)
  {
    if (!state.ToolCalls.TryGetValue(OutputIndexKey(evt), out StreamedToolCall? fragment))
    {
      throw new InvalidOperationException(
          "Function-call arguments delta arrived before its output_item.added event.");
    }

    if (evt.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.String)
    {
      fragment.AppendArguments(delta.GetString()!);
    }
  }

  private static void ApplyArgumentsDone(JsonElement evt, StreamState state)
  {
    if (!state.ToolCalls.TryGetValue(OutputIndexKey(evt), out StreamedToolCall? fragment))
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
  private static void ApplyTerminal(JsonElement evt, StreamState state)
  {
    if (evt.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object)
    {
      state.Usage = ResponsesApiRequestCore.ParseUsage(response);
      state.FinishReason = ParseTerminalFinishReason(response, state.ToolCalls.Count > 0);
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

  private static void ApplyDelta(JsonElement evt, DeltaSink sink, StreamState state)
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
      _ = state.Content.Append(text);
      state.OnContentDelta?.Invoke(text);
    }
    else
    {
      state.OnReasoningDelta?.Invoke(text);
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

  /// <summary>Accumulates one stream's state: the content builder, the streamed tool
  ///     calls keyed by output index, the surfaced server-tool calls keyed by output
  ///     index, the terminal usage and finish reason, and the delta observers. One
  ///     instance rides the whole stream read.</summary>
  private sealed class StreamState
  {
    internal StringBuilder Content { get; } = new();
    internal Dictionary<string, StreamedToolCall> ToolCalls { get; } = []; // keyed by output_index
    internal Dictionary<int, ServerToolCall> ServerToolCalls { get; } = []; // keyed by output_index
    internal FinishReason FinishReason { get; set; } = FinishReason.Stop;
    internal TokenUsage? Usage { get; set; }
    internal Action<string>? OnContentDelta { get; init; }
    internal Action<string>? OnReasoningDelta { get; init; }

    /// <summary>Assembles the final response. Assembled function calls imply
    ///     ToolCalls regardless of what (or whether) the terminal event said — the
    ///     same contract the non-streaming parser applies, so a stream missing its
    ///     terminal frame still dispatches the tool loop.</summary>
    internal ModelResponse ToResponse() => new(
        Content.Length > 0 ? Content.ToString() : null,
        [.. ToolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToRequest())],
        ToolCalls.Count > 0 ? FinishReason.ToolCalls : FinishReason,
        Usage,
        [.. ServerToolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value)]);
  }
}
