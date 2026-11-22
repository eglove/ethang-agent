using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>Reads one Cloudflare Workers AI response stream into a <see cref="ModelResponse"/>.
///     <para>THE FRAME VOCABULARY IS CLOUDFLARE'S OWN, NOT OPENAI'S. A Cloudflare stream frame
///     carries a top-level <c>response</c> string for the content delta, a top-level
///     <c>tool_calls</c> array for tool-call fragments, and <c>usage</c> on whichever frame
///     reports it; <c>[DONE]</c> terminates the stream. The OpenAI-shaped variant
///     (<c>choices[0].delta</c>) is read as well, because Cloudflare serves it for some models,
///     and it is the only place a reasoning delta arrives.</para>
///     <para>TOOL-CALL ARGUMENTS ARRIVE IN FRAGMENTS and are assembled here: a fragment that
///     names a call opens it, later fragments append their argument text to it, and the
///     null-finalization sentinel Cloudflare sends at the end of a call contributes nothing.
///     </para>
///     <para>A MALFORMED FRAME IS A NAMED FAILURE, never a silently shortened answer: a stream
///     this reader cannot parse fails the call, because a partial answer reported as a complete
///     one is worse than an error the caller can act on.</para></summary>
internal sealed class CloudflareStreamReader
{
  /// <summary>The SSE field that carries a frame's payload.</summary>
  private const string DataField = "data:";

  /// <summary>The payload that terminates a stream.</summary>
  private const string DonePayload = "[DONE]";

  private readonly StringBuilder _content = new();
  private readonly StringBuilder _reasoning = new();
  private readonly List<PendingToolCall> _calls = [];
  private readonly Dictionary<int, PendingToolCall> _byIndex = [];
  private readonly long _started;

  private TokenUsage? _usage;
  private FinishReason? _finish;
  private string? _servedModelId;
  private long? _firstTokenMs;

  private CloudflareStreamReader(long started) => _started = started;

  /// <summary>Reads <paramref name="response"/>'s body to completion, emitting every content
  ///     and reasoning delta as it arrives and returning the assembled response.
  ///     <para>The first delta's arrival time is measured from <paramref name="started"/>, the
  ///     timestamp the caller took at dispatch, so the reported time-to-first-token covers the
  ///     whole provider call rather than the part after the headers.</para></summary>
  /// <param name="response">The successful response whose body carries the stream.</param>
  /// <param name="onContentDelta">Receives each content fragment, or null to discard them.
  ///     </param>
  /// <param name="onReasoningDelta">Receives each reasoning fragment, or null to discard them.
  ///     </param>
  /// <param name="started">The dispatch timestamp, from <c>Stopwatch.GetTimestamp</c>.</param>
  /// <param name="ct">Cancellation.</param>
  public static async Task<Result<ModelResponse>> ReadAsync(HttpResponseMessage response,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta, long started,
      CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(response);
    CloudflareStreamReader reader = new(started);
    return await reader.ReadAsync(response, onContentDelta, onReasoningDelta, ct)
        .ConfigureAwait(false);
  }

  /// <summary>Reads the body line by line, translating every data frame.
  ///     </summary>
  /// <param name="response">The response whose body is read.</param>
  /// <param name="onContentDelta">The content sink, or null.</param>
  /// <param name="onReasoningDelta">The reasoning sink, or null.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ModelResponse>> ReadAsync(HttpResponseMessage response,
      Action<string>? onContentDelta, Action<string>? onReasoningDelta, CancellationToken ct)
  {
    using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    using StreamReader lines = new(body, Encoding.UTF8);
    while (await lines.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
    {
      if (!line.StartsWith(DataField, StringComparison.Ordinal))
      {
        // Blank separators, SSE comments (a ': keep-alive' heartbeat) and 'event:' lines carry
        // no payload this reader acts on.
        continue;
      }

      string payload = line[DataField.Length..].Trim();
      if (payload.Length == 0)
      {
        continue;
      }

      if (string.Equals(payload, DonePayload, StringComparison.Ordinal))
      {
        break;
      }

      if (Frame(payload, onContentDelta, onReasoningDelta) is { } fault)
      {
        return Result.Failure<ModelResponse>(fault);
      }
    }

    return Result.Success(Assembled());
  }

  /// <summary>Translates one frame's payload, returning the named failure when it cannot be read.
  ///     </summary>
  /// <param name="payload">The frame's JSON payload.</param>
  /// <param name="onContentDelta">The content sink, or null.</param>
  /// <param name="onReasoningDelta">The reasoning sink, or null.</param>
  private DomainError? Frame(string payload, Action<string>? onContentDelta,
      Action<string>? onReasoningDelta)
  {
    JsonDocument document;
    try
    {
      document = JsonDocument.Parse(payload);
    }
    catch (JsonException ex)
    {
      return new DomainError("ProviderError",
          $"Cloudflare sent a stream frame that is not valid JSON: {ex.Message}");
    }

    using (document)
    {
      JsonElement frame = document.RootElement;
      if (frame.ValueKind != JsonValueKind.Object)
      {
        return new DomainError("ProviderError",
            "Cloudflare sent a stream frame that is not a JSON object.");
      }

      AppendText(frame, "response", _content, onContentDelta);
      AppendToolCalls(frame, "tool_calls");
      AppendChoices(frame, onContentDelta, onReasoningDelta);
      RecordUsage(frame);
      RecordFinish(frame);
      RecordServedModel(frame);
    }

    return null;
  }

  /// <summary>Appends a string member's text to <paramref name="sink"/> and hands it to the
  ///     callback. An absent member, a null, and an empty string contribute nothing.</summary>
  /// <param name="frame">The frame.</param>
  /// <param name="member">The member name.</param>
  /// <param name="sink">The accumulator.</param>
  /// <param name="callback">The sink to notify, or null.</param>
  private void AppendText(JsonElement frame, string member, StringBuilder sink,
      Action<string>? callback)
  {
    if (!frame.TryGetProperty(member, out JsonElement value)
        || value.ValueKind != JsonValueKind.String
        || value.GetString() is not { Length: > 0 } text)
    {
      return;
    }

    _ = sink.Append(text);
    _firstTokenMs ??= Elapsed();
    callback?.Invoke(text);
  }

  /// <summary>Reads the OpenAI-shaped <c>choices[0].delta</c> members: content, a reasoning
  ///     delta, and the frame's finish reason. A frame carrying no choices contributes nothing.
  ///     </summary>
  /// <param name="frame">The frame.</param>
  /// <param name="onContentDelta">The content sink, or null.</param>
  /// <param name="onReasoningDelta">The reasoning sink, or null.</param>
  private void AppendChoices(JsonElement frame, Action<string>? onContentDelta,
      Action<string>? onReasoningDelta)
  {
    if (!frame.TryGetProperty("choices", out JsonElement choices)
        || choices.ValueKind != JsonValueKind.Array
        || choices.GetArrayLength() == 0)
    {
      return;
    }

    JsonElement choice = choices[0];
    if (choice.ValueKind != JsonValueKind.Object)
    {
      return;
    }

    if (choice.TryGetProperty("delta", out JsonElement delta)
        && delta.ValueKind == JsonValueKind.Object)
    {
      AppendText(delta, "content", _content, onContentDelta);
      AppendText(delta, "reasoning_content", _reasoning, onReasoningDelta);
      AppendText(delta, "reasoning", _reasoning, onReasoningDelta);
      AppendToolCalls(delta, "tool_calls");
    }

    if (choice.TryGetProperty("finish_reason", out JsonElement reason)
        && reason.ValueKind == JsonValueKind.String)
    {
      _finish = CloudflareFinishReason.Of(reason.GetString());
    }
  }

  /// <summary>Assembles the tool-call fragments a frame carries.
  ///     <para>A FRAGMENT THAT NAMES A CALL OPENS ONE, and a fragment carrying only arguments
  ///     extends the call it belongs to. Both wire shapes are read: the native
  ///     <c>{name, arguments}</c> and the OpenAI-shaped <c>{index, id, function:{…}}</c>.
  ///     </para>
  ///     <para>THE NULL-FINALIZATION SENTINEL CONTRIBUTES NOTHING: Cloudflare closes a streamed
  ///     call with a fragment naming no id, no name and no arguments, and treating it as a call
  ///     would return an empty tool call the model never made.</para></summary>
  /// <param name="frame">The frame or delta carrying the array.</param>
  /// <param name="member">The member name the array rides under.</param>
  private void AppendToolCalls(JsonElement frame, string member)
  {
    if (!frame.TryGetProperty(member, out JsonElement calls)
        || calls.ValueKind != JsonValueKind.Array)
    {
      return;
    }

    foreach (JsonElement fragment in calls.EnumerateArray())
    {
      if (fragment.ValueKind == JsonValueKind.Object)
      {
        AppendFragment(fragment);
      }
    }
  }

  /// <summary>One tool-call fragment: opens a call when it names one, otherwise extends the
  ///     call it addresses.</summary>
  /// <param name="fragment">The fragment.</param>
  private void AppendFragment(JsonElement fragment)
  {
    JsonElement function = Function(fragment);
    Fragment parts = new(
        StringMember(fragment, "name") ?? StringMember(function, "name"),
        StringMember(fragment, "id"),
        ArgumentsOf(fragment) ?? ArgumentsOf(function),
        IndexOf(fragment));
    if (!parts.IsSentinel)
    {
      parts.Extend(Open(parts.Index, parts.Name is not null));
    }
  }

  /// <summary>The object a fragment states its name and arguments under: the nested
  ///     <c>function</c> object when the fragment carries one, the fragment itself otherwise.
  ///     </summary>
  /// <param name="fragment">The fragment.</param>
  private static JsonElement Function(JsonElement fragment)
      => fragment.TryGetProperty("function", out JsonElement nested)
          && nested.ValueKind == JsonValueKind.Object
              ? nested
              : fragment;

  /// <summary>The call a fragment belongs to: the one its index names, or a new call when the
  ///     fragment names one, or the last call opened when it addresses none.</summary>
  /// <param name="index">The fragment's index, or null when it states none.</param>
  /// <param name="opens">True when the fragment names a call.</param>
  private PendingToolCall Open(int? index, bool opens)
  {
    if (index is { } position && _byIndex.TryGetValue(position, out PendingToolCall? existing))
    {
      return existing;
    }

    if (!opens && _calls.Count > 0 && index is null)
    {
      return _calls[^1];
    }

    PendingToolCall call = new();
    _calls.Add(call);
    _byIndex[index ?? (_calls.Count - 1)] = call;
    return call;
  }

  /// <summary>The fragment's argument value, or null when it carries none.</summary>
  /// <param name="element">The object to read <c>arguments</c> from.</param>
  private static JsonElement? ArgumentsOf(JsonElement element)
      => element.TryGetProperty("arguments", out JsonElement arguments)
          && arguments.ValueKind is JsonValueKind.String or JsonValueKind.Object
              ? arguments
              : null;

  /// <summary>The fragment's index, or null when it states none.</summary>
  /// <param name="fragment">The fragment.</param>
  private static int? IndexOf(JsonElement fragment)
      => fragment.TryGetProperty("index", out JsonElement index)
          && index.ValueKind == JsonValueKind.Number
          && index.TryGetInt32(out int position)
              ? position
              : null;


  /// <summary>Records the usage a frame reports, when it reports one.</summary>
  /// <param name="frame">The frame.</param>
  private void RecordUsage(JsonElement frame)
  {
    if (frame.TryGetProperty("usage", out JsonElement usage)
        && usage.ValueKind == JsonValueKind.Object)
    {
      _usage = CloudflareUsage.Read(usage) ?? _usage;
    }
  }

  /// <summary>Records the finish reason a frame states at the top level, when it states one.
  ///     </summary>
  /// <param name="frame">The frame.</param>
  private void RecordFinish(JsonElement frame)
  {
    if (frame.TryGetProperty("finish_reason", out JsonElement reason)
        && reason.ValueKind == JsonValueKind.String)
    {
      _finish = CloudflareFinishReason.Of(reason.GetString());
    }
  }

  /// <summary>Records the model a frame reports as having served the response, when it reports
  ///     one.</summary>
  /// <param name="frame">The frame.</param>
  private void RecordServedModel(JsonElement frame)
  {
    if (StringMember(frame, "model") is { Length: > 0 } served)
    {
      _servedModelId = served;
    }
  }

  /// <summary>The assembled response: the concatenated text, every tool call in the order the
  ///     model opened it, the reported usage, and the finish reason.
  ///     <para>A RESPONSE THAT NAMED TOOL CALLS FINISHES AS ToolCalls whatever the wire said,
  ///     because the loop must dispatch them.</para></summary>
  private ModelResponse Assembled()
  {
    IReadOnlyList<ToolCallRequest> toolCalls = [.. _calls
        .Where(call => call.Name.Length > 0)
        .Select((call, position) => new ToolCallRequest(
            call.Id ?? $"cloudflare-tool-{position}",
            call.Name,
            call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}"))];
    return new ModelResponse(
        _content.Length > 0 ? _content.ToString() : null,
        toolCalls,
        toolCalls.Count > 0 ? FinishReason.ToolCalls : _finish ?? FinishReason.Stop,
        _usage)
    {
      Reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : null,
      ServedModelId = _servedModelId,
      FirstTokenMs = _firstTokenMs,
    };
  }

  /// <summary>Whole milliseconds since the caller's dispatch timestamp.</summary>
  private long Elapsed()
      => (long)System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;


  /// <summary>A string member's value, or null when the member is absent or not a string.
  ///     </summary>
  /// <param name="element">The object to read.</param>
  /// <param name="member">The member name.</param>
  private static string? StringMember(JsonElement element, string member)
      => element.TryGetProperty(member, out JsonElement value)
          && value.ValueKind == JsonValueKind.String
              ? value.GetString()
              : null;

  /// <summary>One tool-call fragment's facts, read from either wire shape the model may use.
  ///     <para>THE SENTINEL IS THE ABSENCE OF ALL THREE: Cloudflare closes a streamed call with a
  ///     fragment naming no name, no id and no arguments, and treating it as a call would return
  ///     an empty tool call the model never made.</para></summary>
  /// <param name="Name">The call name the fragment states, or null.</param>
  /// <param name="Id">The call id the fragment states, or null.</param>
  /// <param name="Arguments">The argument value the fragment states, or null.</param>
  /// <param name="Index">The call index the fragment states, or null.</param>
  private readonly record struct Fragment(string? Name, string? Id, JsonElement? Arguments, int? Index)
  {
    /// <summary>True when the fragment names nothing at all: the null-finalization sentinel.
    ///     </summary>
    public bool IsSentinel => Name is null && Id is null && Arguments is null;

    /// <summary>Extends <paramref name="call"/> with what this fragment states: a name and an id
    ///     only while the call has neither, and argument text appended to what it already holds.
    ///     </summary>
    /// <param name="call">The call this fragment belongs to.</param>
    public void Extend(PendingToolCall call)
    {
      ArgumentNullException.ThrowIfNull(call);
      if (Name is { Length: > 0 } name && call.Name.Length == 0)
      {
        call.Name = name;
      }

      if (Id is { Length: > 0 } id && call.Id is null)
      {
        call.Id = id;
      }

      if (Arguments is { } arguments)
      {
        _ = call.Arguments.Append(ArgumentText(arguments));
      }
    }

    /// <summary>The argument text a value contributes: a string is appended as it arrived, and
    ///     an object is serialized — Cloudflare's documented shape carries the arguments as an
    ///     object, while the streamed shape carries them as text fragments.</summary>
    /// <param name="arguments">The argument value.</param>
    private static string ArgumentText(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString() ?? ""
            : arguments.GetRawText();
  }

  /// <summary>One tool call being assembled across frames.</summary>
  private sealed class PendingToolCall
  {
    /// <summary>The id the wire named, or null when it named none.</summary>
    public string? Id { get; set; }

    /// <summary>The call's name, empty until a fragment states one.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The argument text accumulated so far.</summary>
    public StringBuilder Arguments { get; } = new();
  }
}
