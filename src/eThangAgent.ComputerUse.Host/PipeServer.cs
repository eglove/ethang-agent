using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Broker launch configuration: the pipe to serve and the auth token.
///     Immutable once resolved by the launcher.</summary>
public sealed record BrokerConfig(string PipeName, string Token)
{
  /// <summary>The NDJSON frame ceiling, mirroring the client's BrokerProtocolConstants:
  ///     frames above it are refused and the connection is closed.</summary>
  public const int MaxFrameBytes = 64 * 1024 * 1024;

  /// <summary>The protocol version this broker speaks and checks at hello.</summary>
  public const int ProtocolVersion = 1;

  /// <summary>The platform string this broker reports and checks at hello.</summary>
  public const string Platform = "windows";
};

/// <summary>The error triple of a failed reply: a lowercase snake wire code, a
///     model-facing message, and optional details (e.g. controller_busy's owner,
///     input_busy's action_sent=false).</summary>
public readonly record struct BrokerWireErrorView(string Code, string Message, string? Details);

/// <summary>One reply the broker produces: exactly one of Result or Error. Errors carry
///     a lowercase snake wire code, a model-facing message, and optional details.</summary>
public readonly record struct BrokerResponse(JsonElement? Result, BrokerWireErrorView? Error)
{
  public static BrokerResponse Ok(JsonElement? result) => new(result, null);

  public static BrokerResponse Fail(string code, string message, string? details = null) => new(null, new BrokerWireErrorView(code, message, details));
};

/// <summary>The observation surface of the broker (capture side). Task 16 ships the seam;
///     task 17 fills it with the UIA walk, window capture, and app identity.</summary>
public interface IBrokerObserver
{
  JsonElement? ListApplications();

  JsonElement? ListWindows(JsonElement? parameters);

  JsonElement? CaptureApp(JsonElement? parameters);

  /// <summary>A3: the lease owner was lost; observation-side bookkeeping resets here in
  ///     task 17's implementation (frame registry, surface lifecycle).</summary>
  void OnOwnerLost(int ownerConnectionId);
};

/// <summary>The broker's request dispatcher and per-connection bookkeeping (task 16).
///     Strict handshake: authenticate (fixed id 0) then hello (protocol/platform check;
///     version_mismatch or not_authorized on failure). ONE controller lease per broker:
///     first takeover wins, everyone else gets controller_busy with details.owner;
///     controller_stop/stop_computer_control releases; the lease auto-expires when the
///     owning connection drops (DropConnection from the serve loop). Physical input
///     methods serialize through InputSerializer: a second input call while one is in
///     flight is rejected BEFORE dispatch with input_busy and action_sent=false in
///     details - nothing queued, nothing dispatched (the client maps it to TIMEOUT
///     retryable per the controller ruling). Unknown methods answer method_not_found;
///     malformed frames answer invalid_request. Reply ids echo request ids verbatim.</summary>
/// <summary>One dispatched raw frame: the reply plus the request id it answers
///     (the serve loop echoes the id verbatim into the reply frame).</summary>
public readonly record struct DispatchedReply(BrokerResponse Response, int RequestId);

public sealed class PipeServer
{
  /// <summary>Methods that drive physical input (task 17 dispatch): they require the
  ///     controller lease and serialize through the InputSerializer gate.</summary>
  private static readonly HashSet<string> InputMethods = new(StringComparer.Ordinal)
  {
    "click", "scroll", "drag", "type_text", "press_key", "hold_key", "paste",
    "element_focus", "element_set_value", "element_perform_action", "element_select_text", "element_press",
  };

  private readonly BrokerConfig _config;
  private readonly Lock _gate = new();
  private readonly HashSet<int> _authenticated = [];

  public PipeServer(BrokerConfig config, IBrokerObserver? observer = null)
  {
    _config = config ?? throw new ArgumentNullException(nameof(config));
    InputSerializer = new InputSerializer();
    Lease = new ControllerLease();
    Observer = observer ?? new SkeletonObserver();
    Lease.OwnerLost += Observer.OnOwnerLost;
  }

  /// <summary>The physical-input serializer: tests poke it directly; Dispatch checks it
  ///     around every input method.</summary>
  internal InputSerializer InputSerializer { get; }

  /// <summary>The observation seam behind list_applications/list_windows/capture_app.</summary>
  internal IBrokerObserver Observer { get; }

  private int _nextConnectionId;

  /// <summary>Mints the next connection id (serve loop calls this once per accepted
  ///     connection); ids are broker-process-lifetime unique.</summary>
  internal int NextConnectionId()
  {
    _gate.Enter();
    try
    {
      return ++_nextConnectionId;
    }
    finally
    {
      _gate.Exit();
    }
  }

  /// <summary>The lease, exposed for tests asserting ownership across connections.</summary>
  internal ControllerLease Lease { get; }

  /// <summary>Handles one decoded request frame. connectionId distinguishes concurrent
  ///     pipe connections; the serve loop assigns one per connection and calls
  ///     <see cref="DropConnection"/> when it ends.</summary>
  public BrokerResponse Dispatch(int id, string method, JsonElement? parameters, int connectionId)
  {
    return method == "authenticate"
      ? DispatchAuthenticate(id, parameters, connectionId)
      : DispatchWhenAuthenticated(id, method, parameters, connectionId);
  }

  /// <summary>The non-handshake path: it requires this connection to be authenticated.</summary>
  private BrokerResponse DispatchWhenAuthenticated(int id, string method, JsonElement? parameters, int connectionId)
  {
    return _authenticated.Contains(connectionId)
      ? DispatchAuthenticated(id, method, parameters, connectionId)
      : BrokerResponse.Fail("not_authorized", $"{method} before authenticate on this connection.");
  }

  /// <summary>The authenticate dispatch: the fixed handshake id is part of the contract.</summary>
  private BrokerResponse DispatchAuthenticate(int id, JsonElement? parameters, int connectionId)
  {
    return id == 0
      ? HandleAuthenticate(parameters, connectionId)
      : BrokerResponse.Fail("invalid_request", "authenticate must carry the fixed handshake id 0.");
  }

  /// <summary>The authenticated dispatch path: routing for hello, controller, observation,
  ///     and input methods once the connection has passed authenticate.</summary>
  private BrokerResponse DispatchAuthenticated(int id, string method, JsonElement? parameters, int connectionId)
  {
    return method switch
    {
      "hello" => HandleHello(parameters),
      "controller_status" => HandleControllerStatus(connectionId),
      "controller_takeover" => HandleControllerTakeover(connectionId),
      "controller_stop" => HandleControllerStop(connectionId),
      "stop_computer_control" => HandleControllerStop(connectionId),
      "list_applications" => BrokerResponse.Ok(Observer.ListApplications() ?? JsonSerializer.SerializeToElement(Array.Empty<object>())),
      "list_windows" => BrokerResponse.Ok(Observer.ListWindows(parameters)),
      "capture_app" => Observer.CaptureApp(parameters) is { } capture ? BrokerResponse.Ok(capture) : BrokerResponse.Fail("unimplemented", "capture_app arrives with the task 17 native surface."),
      _ when InputMethods.Contains(method) => HandleInputMethod(id, method, parameters, connectionId),
      _ => BrokerResponse.Fail("method_not_found", $"unknown method: {method}."),
    };
  }


  /// <summary>Handles one RAW frame line: parsing strictness (invalid JSON, missing or
  ///     non-integer id, missing method, non-object frame) answers invalid_request and
  ///     the connection stays - the client decides whether to continue.</summary>
  public DispatchedReply DispatchRaw(string frame, int connectionId)
  {
    JsonElement root;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(frame);
      root = doc.RootElement.Clone();
    }
    catch (JsonException)
    {
      return new DispatchedReply(BrokerResponse.Fail("invalid_request", "frame is not valid JSON."), -1);
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
      return new DispatchedReply(BrokerResponse.Fail("invalid_request", "frame must be a JSON object."), -1);
    }

    if (!root.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int id))
    {
      return new DispatchedReply(BrokerResponse.Fail("invalid_request", "frame is missing an integer id."), -1);
    }

    if (!root.TryGetProperty("method", out JsonElement methodEl) || methodEl.ValueKind != JsonValueKind.String)
    {
      return new DispatchedReply(BrokerResponse.Fail("invalid_request", "frame is missing the method name."), id);
    }

    JsonElement? parameters = root.TryGetProperty("params", out JsonElement paramsEl) && paramsEl.ValueKind == JsonValueKind.Object
      ? paramsEl
      : null;
    return new DispatchedReply(Dispatch(id, methodEl.GetString()!, parameters, connectionId), id);
  }

  /// <summary>The owning connection dropped: the lease auto-expires (raising OwnerLost
  ///     to the input layer and observer) and the connection's authenticated mark clears.</summary>
  public void DropConnection(int connectionId)
  {
    _gate.Enter();
    try
    {
      _ = _authenticated.Remove(connectionId);
    }
    finally
    {
      _gate.Exit();
    }

    _ = Lease.Release(connectionId);
  }

  private BrokerResponse HandleAuthenticate(JsonElement? parameters, int connectionId)
  {
    if (parameters is not { } p || !p.TryGetProperty("token", out JsonElement tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
    {
      return BrokerResponse.Fail("invalid_request", "authenticate requires params.token (string).");
    }

    if (!string.Equals(tokenEl.GetString(), _config.Token, StringComparison.Ordinal))
    {
      return BrokerResponse.Fail("not_authorized", "authenticate failed: token rejected.");
    }

    _gate.Enter();
    try
    {
      _ = _authenticated.Add(connectionId);
    }
    finally
    {
      _gate.Exit();
    }

    return BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { ok = true }));
  }

  private static BrokerResponse HandleHello(JsonElement? parameters)
  {
    if (parameters is not { } p
        || !p.TryGetProperty("protocolVersion", out JsonElement protoEl) || protoEl.ValueKind != JsonValueKind.Number || !protoEl.TryGetInt32(out int protocol)
        || !p.TryGetProperty("platform", out JsonElement platformEl) || platformEl.ValueKind != JsonValueKind.String)
    {
      return BrokerResponse.Fail("invalid_request", "hello requires params.protocolVersion (integer) and params.platform (string).");
    }

    if (protocol != BrokerConfig.ProtocolVersion)
    {
      return BrokerResponse.Fail("version_mismatch", $"protocol version mismatch: client {protocol}, broker {BrokerConfig.ProtocolVersion}.");
    }

    string platform = platformEl.GetString()!;
    return string.Equals(platform, BrokerConfig.Platform, StringComparison.OrdinalIgnoreCase)
      ? BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { protocol = BrokerConfig.ProtocolVersion, platform = BrokerConfig.Platform }))
      : BrokerResponse.Fail("version_mismatch", $"platform mismatch: client {platform}, broker {BrokerConfig.Platform}.");
  }

  private BrokerResponse HandleControllerStatus(int connectionId)
  {
    int? owner = Lease.Owner;
    JsonElement result = JsonSerializer.SerializeToElement(new
    {
      owned = owner == connectionId,
      owner,
    });
    return BrokerResponse.Ok(result);
  }

  private BrokerResponse HandleControllerTakeover(int connectionId)
  {
    LeaseAcquireResult result = Lease.TryAcquire(connectionId);
    return result.Acquired
      ? BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { owned = true }))
      : BrokerResponse.Fail("controller_busy", "another connection holds the controller lease.", result.Owner.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  private BrokerResponse HandleControllerStop(int connectionId)
  {
    return Lease.Release(connectionId)
      ? BrokerResponse.Ok(JsonSerializer.SerializeToElement(new { owned = false }))
      : BrokerResponse.Fail("controller_busy", "this connection does not hold the controller lease.");
  }

  /// <summary>The input-method envelope (task 16 skeleton): the lease gate, then the
  ///     serialization gate, then task 17's dispatcher performs the physical action.
  ///     On the skeleton every input method answers unimplemented AFTER the gates -
  ///     the gates are the task-16 deliverable; the dispatch is task 17's.</summary>
  private BrokerResponse HandleInputMethod(int id, string method, JsonElement? parameters, int connectionId)
  {
    if (Lease.Owner != connectionId)
    {
      return BrokerResponse.Fail("controller_busy", "input requires the controller lease; take over first.");
    }

    using InputOperation? operation = InputSerializer.TryBegin(method);
    return operation is not null
      ? InputRouter.Execute(id, method, parameters)
      : BrokerResponse.Fail(
        "input_busy",
        $"another input operation ({InputSerializer.CurrentMethod}) is in flight; nothing was dispatched.",
        "action_sent=false");
  }



  /// <summary>The default observer: capture methods answer unimplemented until task 17
  ///     supplies the real walk/capture implementation at composition.</summary>
  internal sealed class SkeletonObserver : IBrokerObserver
  {
    public JsonElement? ListApplications() => null;

    public JsonElement? ListWindows(JsonElement? parameters) => null;

    public JsonElement? CaptureApp(JsonElement? parameters) => null;

    public void OnOwnerLost(int ownerConnectionId) { }
  }
};
