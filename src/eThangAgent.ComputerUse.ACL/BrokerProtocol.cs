using System.Text.Json;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Wire frame records (spec 7): requests <c>{id, method, params}</c>, replies
///     <c>{id, result}</c> or <c>{id, error:{code, message, details}}</c>. Ids are monotonic
///     from 1 for requests; the authenticate frame carries the fixed id 0. Parsing is
///     strict: a reply frame without an id, or with neither result nor error, is a
///     protocol violation the caller reports as a transport failure.</summary>
public static class BrokerProtocolConstants
{
  /// <summary>The NDJSON frame ceiling: frames above it are rejected and the connection
  ///     is closed (the wire carries observations and screenshots, never unbounded payloads).</summary>
  public const int MaxFrameBytes = 64 * 1024 * 1024;
}

/// <summary>One outgoing request frame.</summary>
public sealed record BrokerRequest(int Id, string Method, JsonElement? Params)
{
  /// <summary>The authenticate frame: always id 0, per the handshake contract.</summary>
  public static BrokerRequest Authenticate(string token) => new(0, "authenticate",
      JsonSerializer.SerializeToElement(new AuthenticateParams(token), WireOptions));

  /// <summary>A normal request frame with the next monotonic id.</summary>
  public static BrokerRequest Create(int id, string method, JsonElement? parameters) => new(id, method, parameters);

  internal static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

  public string ToJson() => Params is { } p
      ? JsonSerializer.Serialize(new BrokerRequestWire(Id, Method, p), WireOptions)
      : JsonSerializer.Serialize(new BrokerRequestNoParams(Id, Method), WireOptions);

  private sealed record BrokerRequestWire(int Id, string Method, JsonElement Params);
  private sealed record BrokerRequestNoParams(int Id, string Method);
}

/// <summary>authenticate params: the per-spawn shared token.</summary>
public sealed record AuthenticateParams(string Token);

/// <summary>hello params: the protocol version this client speaks.</summary>
public sealed record HelloParams(int ProtocolVersion, string Platform);

/// <summary>One incoming reply frame, parsed to its discriminated shape. A reply carries
///     exactly one of Result or Error; Error is non-null with a wire code and message.</summary>
public sealed record BrokerReply(int Id, JsonElement? Result, BrokerWireError? Error)
{
  /// <summary>Parses one reply frame; null when the frame is not a well-formed reply
  ///     (missing id, or neither result nor error present).</summary>
  public static BrokerReply? Parse(string json)
  {
    JsonElement el;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      el = doc.RootElement.Clone();
    }
    catch (JsonException)
    {
      return null;
    }

    if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("id", out JsonElement idEl)
        || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int id))
    {
      return null;
    }

    JsonElement? result = el.TryGetProperty("result", out JsonElement r) ? r : null;
    BrokerWireError? error = null;
    if (el.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
    {
      string? code = e.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
      string? message = e.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
      if (code is null)
      {
        return null;
      }

      string? details = e.TryGetProperty("details", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
      error = new BrokerWireError(code, message ?? "", details);
    }

    return result is null && error is null ? null : new BrokerReply(id, result, error);
  }

  /// <summary>True when the reply carries a result; on true, result is set.</summary>
  public bool TryGetResult(out JsonElement? result)
  {
    result = Result;
    return Result is not null && Error is null;
  }
}

/// <summary>The error object of a failed reply: the wire code, a human message, and an
///     optional details payload.</summary>
public sealed record BrokerWireError(string Code, string Message, string? Details);

