using System.Diagnostics;
using System.Net;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Provider.Wire;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Cloudflare.ACL;

/// <summary>Implements <see cref="IModelProvider"/> over Cloudflare Workers AI's native run
///     endpoint.
///     <para>THE NATIVE ENDPOINT, NOT THE OPENAI-COMPATIBLE ONE: the compatible surface serves
///     two models and does not stream, so a provider built on it could not serve the catalog.
///     The native endpoint names the model in the PATH — <c>/accounts/{account_id}/ai/run/{model}</c>
///     — and takes a chat body of <c>messages</c> plus sampling knobs.</para>
///     <para>ONE MODEL PER REQUEST. The model id travels in the path and nowhere else; there is
///     no models array, no provider block, and no routing-variant suffix, because Cloudflare has
///     none of those concepts. A config that names a fallback list is served by the harness's
///     walk, never by this provider.</para>
///     <para>ONE ATTEMPT PER CALL. This provider never retries: a rate-limited model is a
///     verdict the harness's walk acts on, and retrying inside the ACL would delay the drop it
///     is supposed to make.</para>
///     <para>EVERY FAILURE IS A NAMED <see cref="DomainError"/> CARRYING CLOUDFLARE'S OWN TEXT,
///     capped the same way the catalog caps it. A body this provider cannot read is a named
///     failure, never an empty successful response.</para></summary>
/// <param name="http">The HTTP client every call goes through.</param>
/// <param name="config">The account, token, and host this provider calls.</param>
public sealed class CloudflareModelProvider(HttpClient http, CloudflareConfiguration config)
    : IModelProvider
{
  /// <summary>The credential was refused: a configuration fault, never a busy pool.</summary>
  private const string ProviderUnauthorized = "ProviderUnauthorized";

  /// <summary>The provider failed in a way that is not about the model.</summary>
  private const string ProviderError = "ProviderError";

  /// <summary>The model is rate limited. Cloudflare answers 429 both for a spent daily
  ///     allocation and for a per-minute limit, and this provider does not claim which.
  ///     </summary>
  private const string RateLimited = "RateLimited";

  /// <summary>The model refused this request.</summary>
  private const string ModelRejected = "ModelRejected";

  /// <summary>The call ran out of time.</summary>
  private const string ProviderTimeout = "ProviderTimeout";

  /// <summary>The request exceeded the model's context window.</summary>
  private const string ContextWindowExceeded = "ContextWindowExceeded";

  /// <summary>A request this provider cannot send as it stands.</summary>
  private const string InvalidRequest = "InvalidRequest";

  /// <summary>The request carries an image and the model takes none.</summary>
  private const string ImageInputUnsupported = "ImageInputUnsupported";

  /// <summary>Substrings that identify a context-window overflow in a 400 body. Matched
  ///     case-insensitively, because providers capitalize inconsistently.</summary>
  private static readonly string[] ContextOverflowMarkers =
  [
      "maximum context length",
      "context_length_exceeded",
      "context length exceeded",
      "reduce the length of the messages",
      "prompt is too long",
      "exceeds the maximum token count",
      "input length and max_tokens exceed context limit",
      "context window",
  ];

  private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
  private readonly CloudflareConfiguration _config =
      config ?? throw new ArgumentNullException(nameof(config));

  /// <summary>The account-relative path a model's run is served on.</summary>
  private const string RunPathPrefix = "ai/run/";

  /// <inheritdoc />
  public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(config);
    if (!_config.IsConfigured)
    {
      return Result.Failure<ModelResponse>(Unconfigured());
    }

    Result<Dictionary<string, object?>> body = BuildBody(config, request, stream: false);
    return body.IsSuccess
        ? await SendOnceAsync(config, body.Value, stream: false, null, null, ct)
            .ConfigureAwait(false)
        : Result.Failure<ModelResponse>(body.Error);
  }

  /// <inheritdoc />
  public async Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config,
      ModelRequest request, Action<string>? onContentDelta = null,
      Action<string>? onReasoningDelta = null, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(config);
    if (!_config.IsConfigured)
    {
      return Result.Failure<ModelResponse>(Unconfigured());
    }

    Result<Dictionary<string, object?>> body = BuildBody(config, request, stream: true);
    return body.IsSuccess
        ? await SendOnceAsync(config, body.Value, stream: true, onContentDelta, onReasoningDelta,
            ct).ConfigureAwait(false)
        : Result.Failure<ModelResponse>(body.Error);
  }

  /// <summary>Sends one request and translates the answer, streaming the body when the caller
  ///     asked for a stream and the response is one.
  ///     <para>A SERVER THAT IGNORES THE STREAM FLAG IS READ AS A PLAIN BODY: the response is
  ///     parsed exactly as the non-streaming path parses it, so a transport fallback never
  ///     changes the parsing rules.</para></summary>
  /// <param name="config">The model this call names.</param>
  /// <param name="body">The built request body.</param>
  /// <param name="stream">True when the caller asked for a stream.</param>
  /// <param name="onContentDelta">The content sink, or null.</param>
  /// <param name="onReasoningDelta">The reasoning sink, or null.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ModelResponse>> SendOnceAsync(ModelConfig config,
      Dictionary<string, object?> body, bool stream, Action<string>? onContentDelta,
      Action<string>? onReasoningDelta, CancellationToken ct)
  {
    long started = Stopwatch.GetTimestamp();
    try
    {
      using HttpRequestMessage request = NewRequest(config, body);
      using HttpResponseMessage response = await _http
          .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
      if (!response.IsSuccessStatusCode)
      {
        return await StatusFailureAsync(response, ct).ConfigureAwait(false);
      }

      Result<ModelResponse> parsed = stream && IsEventStream(response)
          ? await CloudflareStreamReader.ReadAsync(response, onContentDelta, onReasoningDelta,
              started, ct).ConfigureAwait(false)
          : await ReadJsonBodyAsync(response, ct).ConfigureAwait(false);
      return Stamp(parsed, started);
    }
    catch (OperationCanceledException)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderTimeout,
          "Cloudflare did not answer before the call was cancelled."));
    }
    catch (HttpRequestException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError, ex.Message));
    }
    catch (IOException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"the connection to Cloudflare was lost while reading the response: {ex.Message}"));
    }
  }

  /// <summary>The named failure an absent credential earns. A container can outlive the
  ///     credential it was built with, and a call that names no account is a configuration fact
  ///     rather than a transport fault.</summary>
  private static DomainError Unconfigured() => new(ProviderUnauthorized,
      "Cloudflare needs both an account id and an API token before a model can be called.");

  /// <summary>The outbound request: the model in the path, the token as a bearer credential,
  ///     and the body as JSON. The caller owns the returned instance and disposes it.</summary>
  /// <param name="config">The model this call names.</param>
  /// <param name="body">The built request body.</param>
  private HttpRequestMessage NewRequest(ModelConfig config, Dictionary<string, object?> body)
  {
    HttpRequestMessage request = new(HttpMethod.Post, EndpointFor(config.ModelId))
    {
      Content = JsonContent.Create(body),
    };
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.EffectiveApiToken);
    return request;
  }

  /// <summary>The account-scoped run endpoint for one model. The model id travels as a path
  ///     segment, which is where Cloudflare reads it.</summary>
  /// <param name="modelId">The model to call.</param>
  private Uri EndpointFor(string modelId) => _config.Endpoint(RunPathPrefix + modelId);

  /// <summary>True when the response is an event stream rather than a single JSON body.
  ///     </summary>
  /// <param name="response">The response.</param>
  private static bool IsEventStream(HttpResponseMessage response)
      => string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream",
          StringComparison.OrdinalIgnoreCase);

  /// <summary>Stamps a successful response with how long the call took. A failure is returned
  ///     untouched: a failed call produced no response to describe.</summary>
  /// <param name="result">The parsed result.</param>
  /// <param name="started">The dispatch timestamp.</param>
  private static Result<ModelResponse> Stamp(Result<ModelResponse> result, long started)
      => result.IsSuccess
          ? Result.Success(result.Value with
          {
            DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
          })
          : result;

  /// <summary>Builds the request body: the message list, the sampling knobs, the tools, and the
  ///     stream flag. Every named refusal happens before a request is built, so a request this
  ///     provider will not send costs no round trip.</summary>
  /// <param name="config">The model this call names.</param>
  /// <param name="request">The turn's request.</param>
  /// <param name="stream">True when the caller asked for a stream.</param>
  private static Result<Dictionary<string, object?>> BuildBody(ModelConfig config,
      ModelRequest request, bool stream)
  {
    if (string.IsNullOrWhiteSpace(config.ModelId))
    {
      return Result.Failure<Dictionary<string, object?>>(new DomainError(InvalidRequest,
          "the request names no Cloudflare model."));
    }

    if (CarriesImage(request) && !config.AcceptsImageInput)
    {
      return Result.Failure<Dictionary<string, object?>>(new DomainError(ImageInputUnsupported,
          $"the request carries an image and {config.ModelId} does not accept image input."));
    }

    Result<List<object>> messages = Messages(request);
    if (!messages.IsSuccess)
    {
      return Result.Failure<Dictionary<string, object?>>(messages.Error);
    }

    Dictionary<string, object?> body = new() { ["messages"] = messages.Value };
    ApplySampling(body, config);
    ApplyTools(body, request);
    if (stream)
    {
      body["stream"] = true;
    }

    return Result.Success(body);
  }

  /// <summary>Writes the sampling knobs the model config states. A knob the config leaves null
  ///     is omitted so Cloudflare's own default applies, rather than being sent as a value the
  ///     user never chose.</summary>
  /// <param name="body">The body being built.</param>
  /// <param name="config">The model this call names.</param>
  private static void ApplySampling(Dictionary<string, object?> body, ModelConfig config)
  {
    body["max_tokens"] = config.MaxTokens;
    body["temperature"] = config.Temperature;
    AddWhenSet(body, "top_p", config.TopP);
    AddWhenSet(body, "top_k", config.TopK);
    AddWhenSet(body, "seed", config.Seed);
    AddWhenSet(body, "frequency_penalty", config.FrequencyPenalty);
    AddWhenSet(body, "presence_penalty", config.PresencePenalty);
    AddWhenSet(body, "repetition_penalty", config.RepetitionPenalty);
  }

  /// <summary>Writes a knob only when the config states one.</summary>
  /// <param name="body">The body being built.</param>
  /// <param name="member">The wire member name.</param>
  /// <param name="value">The value, or null to omit the member.</param>
  private static void AddWhenSet<T>(Dictionary<string, object?> body, string member, T? value)
      where T : struct
  {
    if (value is { } stated)
    {
      body[member] = stated;
    }
  }

  /// <summary>Writes the tool array when the request advertises tools: Cloudflare's flat
  ///     function shape, with the parameter schema and the required subset.</summary>
  /// <param name="body">The body being built.</param>
  /// <param name="request">The turn's request.</param>
  private static void ApplyTools(Dictionary<string, object?> body, ModelRequest request)
  {
    if (request.Tools is { Count: > 0 } tools)
    {
      body["tools"] = new List<object>(tools.Select(ToolEntry));
    }
  }

  /// <summary>One tool in Cloudflare's flat function shape.</summary>
  /// <param name="tool">The tool's advertised contract.</param>
  private static Dictionary<string, object?> ToolEntry(ToolDefinition tool)
      => new()
      {
        ["type"] = "function",
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["parameters"] = new Dictionary<string, object?>
        {
          ["type"] = "object",
          ["properties"] = tool.Parameters.ToDictionary(
              parameter => parameter.Name,
              parameter => (object)ParameterSchema(parameter)),
          ["required"] = tool.RequiredParameters.ToArray(),
        },
      };

  /// <summary>One parameter's JSON schema.</summary>
  /// <param name="parameter">The parameter's advertised contract.</param>
  private static Dictionary<string, object?> ParameterSchema(ToolParameter parameter)
  {
    Dictionary<string, object?> schema = new()
    {
      ["type"] = SchemaType(parameter.Type),
      ["description"] = parameter.Description,
    };
    if (parameter.Type == ToolParameterType.TextArray)
    {
      schema["items"] = new Dictionary<string, object?> { ["type"] = "string" };
    }

    if (parameter.Type == ToolParameterType.ObjectArray)
    {
      schema["items"] = new Dictionary<string, object?> { ["type"] = "object" };
    }

    if (parameter.Minimum is { } minimum)
    {
      schema["minimum"] = minimum;
    }

    return schema;
  }

  /// <summary>The JSON schema type a harness parameter type states.</summary>
  /// <param name="type">The parameter type.</param>
  private static string SchemaType(ToolParameterType type) => type switch
  {
    ToolParameterType.Text => "string",
    ToolParameterType.TextArray => "array",
    ToolParameterType.WholeNumber => "integer",
    ToolParameterType.Flag => "boolean",
    ToolParameterType.ObjectValue => "object",
    ToolParameterType.ObjectArray => "array",
    _ => throw new ArgumentOutOfRangeException(nameof(type), type,
        "Every parameter type must state its own schema type."),
  };

  /// <summary>True when the request carries an image whose bytes this call would send.
  ///     <para>A WITHHELD IMAGE IS NOT CARRIED: an image from an earlier turn renders as a
  ///     marker, so it needs no image support from the model. Only a part the resend plan sends
  ///     makes the request an image-bearing one.</para></summary>
  /// <param name="request">The turn's request.</param>
  private static bool CarriesImage(ModelRequest request)
  {
    ImageResendPlan plan = ImageResendPlan.For(request.Messages);
    for (int index = 0; index < request.Messages.Count; index++)
    {
      if (request.Messages[index].Parts is { Count: > 0 } parts
          && parts.Any(part => plan.Sends(part, index)))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>The wire message list.
  ///     <para>THE SYSTEM PROMPT RIDES AS A SYSTEM MESSAGE: Cloudflare's chat body carries
  ///     instructions as a message, not as a top-level field.</para>
  ///     <para>A TOOL RESULT WITH NO CALL ID IS A NAMED FAILURE: the wire attributes a result to
  ///     a call by id, so a result that names none cannot be sent.</para></summary>
  /// <param name="request">The turn's request.</param>
  private static Result<List<object>> Messages(ModelRequest request)
  {
    List<object> messages = [];
    if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
    {
      messages.Add(MessageEntry("system", request.SystemPrompt));
    }

    ImageResendPlan plan = ImageResendPlan.For(request.Messages);
    for (int index = 0; index < request.Messages.Count; index++)
    {
      Message message = request.Messages[index];
      if (message.Role == Role.Tool && string.IsNullOrEmpty(message.ToolCallId))
      {
        return Result.Failure<List<object>>(new DomainError(InvalidRequest,
            "a tool message carries no call id, so Cloudflare cannot attribute its result."));
      }

      messages.Add(WireMessage(message, plan, index));
    }

    return Result.Success(messages);
  }

  /// <summary>One conversation message on the wire.</summary>
  /// <param name="message">The message.</param>
  /// <param name="plan">The resend decision for the whole request.</param>
  /// <param name="index">The message's index in the request.</param>
  private static Dictionary<string, object?> WireMessage(Message message, ImageResendPlan plan,
      int index)
  {
    Dictionary<string, object?> wire = new()
    {
      ["role"] = RoleName(message.Role),
      ["content"] = Content(message, plan, index),
    };
    if (message.Role == Role.Tool)
    {
      wire["tool_call_id"] = message.ToolCallId;
    }

    if (message.ToolCalls is { Count: > 0 } calls)
    {
      wire["tool_calls"] = new List<object>(calls.Select(WireToolCall));
    }

    return wire;
  }

  /// <summary>The wire name of a message role.</summary>
  /// <param name="role">The role.</param>
  private static string RoleName(Role role) => role switch
  {
    Role.System => "system",
    Role.User => "user",
    Role.Assistant => "assistant",
    Role.Tool => "tool",
    _ => throw new ArgumentOutOfRangeException(nameof(role), role,
        "Every role must state its own wire name."),
  };

  /// <summary>One assistant tool call on the wire: the id, the function name, and the arguments
  ///     as the JSON text Cloudflare reads.</summary>
  /// <param name="call">The call the model made.</param>
  private static Dictionary<string, object?> WireToolCall(ToolCall call)
      => new()
      {
        ["id"] = call.Id,
        ["type"] = "function",
        ["function"] = new Dictionary<string, object?>
        {
          ["name"] = call.Name,
          ["arguments"] = call.Arguments,
        },
      };

  /// <summary>A message's content: the plain text, or an input-part array when the message
  ///     carries an image whose bytes this call sends.
  ///     <para>A WITHHELD OR STORED IMAGE RENDERS AS ITS MARKER, the same rendering every other
  ///     parts consumer emits, so the history still records that a picture was there.</para>
  ///     </summary>
  /// <param name="message">The message.</param>
  /// <param name="plan">The resend decision for the whole request.</param>
  /// <param name="index">The message's index in the request.</param>
  private static object Content(Message message, ImageResendPlan plan, int index)
  {
    if (message.Parts is not { Count: > 0 } parts)
    {
      return message.Content;
    }

    List<object> content = [.. parts.Select(part => PartEntry(part, plan, index))];
    bool hasText = parts.Any(part => part is MessagePart.TextPart)
        || string.IsNullOrEmpty(message.Content);
    if (!hasText)
    {
      content.Insert(0, TextEntry(message.Content));
    }

    return content;
  }

  /// <summary>One content part on the wire.</summary>
  /// <param name="part">The part.</param>
  /// <param name="plan">The resend decision for the whole request.</param>
  /// <param name="index">The owning message's index in the request.</param>
  private static Dictionary<string, object?> PartEntry(MessagePart part, ImageResendPlan plan,
      int index) => part switch
      {
        MessagePart.TextPart text => TextEntry(text.Text),
        MessagePart.ImagePart image when plan.Sends(image, index) => ImageEntry(image),
        MessagePart.ImagePart withheld => TextEntry(MarkerForNotResent(withheld)),
        MessagePart.ImageReferencePart stored => TextEntry(
            ImagePartMarker.MarkerForStored(stored.MediaType, stored.Path)),
        _ => throw new InvalidOperationException(
            $"Unhandled message part type: {part.GetType().Name}."),
      };

  /// <summary>The marker an image renders as when this call does not send its bytes.</summary>
  /// <param name="image">The withheld image part.</param>
  private static string MarkerForNotResent(MessagePart.ImagePart image)
      => ImagePartMarker.MarkerForNotResent(image.MediaType,
          ImagePartMarker.DecodedByteCount(image.Base64Data));

  /// <summary>One text part on the wire.</summary>
  /// <param name="text">The text.</param>
  private static Dictionary<string, object?> TextEntry(string text)
      => new() { ["type"] = "text", ["text"] = text };

  /// <summary>One message whose content is a single text string.</summary>
  /// <param name="role">The wire role name.</param>
  /// <param name="text">The message text.</param>
  private static Dictionary<string, object?> MessageEntry(string role, string text)
      => new() { ["role"] = role, ["content"] = text };

  /// <summary>One image part on the wire, as the data URL Cloudflare reads.</summary>
  /// <param name="image">The image part.</param>
  private static Dictionary<string, object?> ImageEntry(MessagePart.ImagePart image)
      => new()
      {
        ["type"] = "image_url",
        ["image_url"] = new Dictionary<string, object?>
        {
          ["url"] = ImageDataUrl.For(image.MediaType, image.Base64Data),
        },
      };

  /// <summary>Reads a non-streaming body: Cloudflare's envelope carries the payload under
  ///     <c>result</c>.
  ///     <para>A BODY THAT CANNOT BE READ IS A NAMED FAILURE, never an empty successful
  ///     response: a turn that reported success with no content would look like a model that
  ///     said nothing.</para></summary>
  /// <param name="response">The successful response.</param>
  /// <param name="ct">Cancellation.</param>
  private static async Task<Result<ModelResponse>> ReadJsonBodyAsync(HttpResponseMessage response,
      CancellationToken ct)
  {
    string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    try
    {
      using JsonDocument document = JsonDocument.Parse(body);
      return document.RootElement.ValueKind == JsonValueKind.Object
          && document.RootElement.TryGetProperty("result", out JsonElement result)
              ? Result.Success(CloudflareResponse.Read(result))
              : Result.Failure<ModelResponse>(new DomainError(ProviderError,
                  "Cloudflare's response carries no 'result' member."));
    }
    catch (JsonException ex)
    {
      return Result.Failure<ModelResponse>(new DomainError(ProviderError,
          $"Cloudflare's response is not valid JSON: {ex.Message}"));
    }
  }

  /// <summary>Reads the failed response's body and maps its status to a named failure.
  ///     </summary>
  /// <param name="response">The failed response.</param>
  /// <param name="ct">Cancellation.</param>
  private static async Task<Result<ModelResponse>> StatusFailureAsync(HttpResponseMessage response,
      CancellationToken ct)
  {
    string body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
    return StatusFailure(response.StatusCode, CloudflareErrorText.Of(body));
  }

  /// <summary>The failed response's body, or an empty string when it cannot be read: a read
  ///     fault never replaces the failure with one of its own.</summary>
  /// <param name="response">The failed response.</param>
  /// <param name="ct">Cancellation.</param>
  private static async Task<string> ReadBodyAsync(HttpResponseMessage response,
      CancellationToken ct)
  {
    try
    {
      return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException
        or InvalidOperationException or NotSupportedException or ObjectDisposedException)
    {
      return string.Empty;
    }
  }

  /// <summary>Maps an HTTP status to its named failure, carrying Cloudflare's own text.
  ///     <para>A 429 IS A RATE LIMIT AND NOTHING MORE: Cloudflare answers 429 both for a spent
  ///     daily allocation and for a per-minute limit, so this provider names the status and
  ///     quotes Cloudflare's message rather than claiming which limit was reached.</para>
  ///     <para>401 AND 403 ARE A REFUSED CREDENTIAL, which is a configuration fault the user
  ///     fixes rather than a model verdict the walk may skip.</para></summary>
  /// <param name="status">The status the response carried.</param>
  /// <param name="detail">Cloudflare's own error text.</param>
  private static Result<ModelResponse> StatusFailure(HttpStatusCode status, string detail)
      => (int)status switch
      {
        429 => Failure(RateLimited, status, detail),
        401 or 403 => Failure(ProviderUnauthorized, status, detail),
        408 => Result.Failure<ModelResponse>(new DomainError(ProviderTimeout,
            Detail(status, detail))),
        400 when IsContextOverflow(detail) => Failure(ContextWindowExceeded, status, detail),
        400 or 404 => Failure(ModelRejected, status, detail),
        _ => Failure(ProviderError, status, detail),
      };

  /// <summary>A named failure carrying the status and Cloudflare's text.</summary>
  /// <param name="code">The failure code.</param>
  /// <param name="status">The status the response carried.</param>
  /// <param name="detail">Cloudflare's own error text.</param>
  private static Result<ModelResponse> Failure(string code, HttpStatusCode status, string detail)
      => Result.Failure<ModelResponse>(new DomainError(code, Detail(status, detail)));

  /// <summary>The failure message: the status and Cloudflare's own text.</summary>
  /// <param name="status">The status the response carried.</param>
  /// <param name="detail">Cloudflare's own error text, possibly empty.</param>
  private static string Detail(HttpStatusCode status, string detail)
      => detail.Length == 0
          ? $"Cloudflare returned HTTP {(int)status}."
          : $"Cloudflare returned HTTP {(int)status}: {detail}";

  /// <summary>True when the error body names the context-length fault. A body that cannot be
  ///     read is not an overflow: classification stays conservative.</summary>
  /// <param name="detail">Cloudflare's own error text.</param>
  private static bool IsContextOverflow(string detail)
      => ContextOverflowMarkers.Any(marker =>
          detail.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
