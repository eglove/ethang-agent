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

  internal static readonly JsonSerializerOptions WireOptions = BrokerWire.Options;

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
}

/// <summary>Wire JSON options: camelCase names, relaxed string escaping so multi-byte
///     UTF-8 (window titles, values) travels as real UTF-8 bytes, not escape sequences.
///     The NDJSON frame decode must therefore be true UTF-8, never per-byte chars.</summary>
public static class BrokerWire
{
  public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
  {
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };
}

/// <summary>The error object of a failed reply: the wire code, a human message, and an
///     optional details payload.</summary>
public sealed record BrokerWireError(string Code, string Message, string? Details);


// ---- Typed result records (spec 7): capture_app results and action receipts ----

/// <summary>capture_app result (spec 7): the observation ledger row the broker returns.
///     All field names mirror the wire keys exactly. From(BrokerReply) parses the reply's
///     result object; a wrong-shape result returns null (fail-closed), never a guess.</summary>
public sealed record CaptureAppResult(
  string StateId,
  string SnapshotMode,
  string? BaseStateId,
  CaptureAppApp App,
  CaptureAppWindow Window,
  IReadOnlyList<CaptureAppElement> Elements,
  CaptureAppScreenshot? Screenshot,
  string? EffectEvidence)
{
  /// <summary>Parses the reply's result as a capture_app result; null when the result is
  ///     absent, not an object, or missing required keys of the wrong kinds.</summary>
  public static CaptureAppResult? From(BrokerReply reply)
  {
    ArgumentNullException.ThrowIfNull(reply);
    if (reply.Error is not null || reply.Result is not { } root || root.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    string? stateId = Wire.Str(root, "state_id");
    string? snapshotMode = Wire.Str(root, "snapshot_mode");
    if (stateId is null || snapshotMode is not ("full" or "delta" or "no_change")
        || !root.TryGetProperty("app", out JsonElement appEl) || appEl.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("window", out JsonElement windowEl) || windowEl.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    CaptureAppApp? app = CaptureAppApp.From(appEl);
    CaptureAppWindow? window = CaptureAppWindow.From(windowEl);
    if (app is null || window is null)
    {
      return null;
    }

    List<CaptureAppElement> elements = [];
    if (root.TryGetProperty("elements", out JsonElement elementsEl) && elementsEl.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement el in elementsEl.EnumerateArray())
      {
        CaptureAppElement? element = CaptureAppElement.From(el);
        if (element is null)
        {
          return null;
        }

        elements.Add(element);
      }
    }

    CaptureAppScreenshot? screenshot = null;
    if (root.TryGetProperty("screenshot", out JsonElement shotEl) && shotEl.ValueKind == JsonValueKind.Object)
    {
      screenshot = CaptureAppScreenshot.From(shotEl);
      if (screenshot is null)
      {
        return null;
      }
    }

    return new CaptureAppResult(stateId, snapshotMode, Wire.StrOr(root, "base_state_id"), app, window,
      elements, screenshot, Wire.StrOr(root, "effect_evidence"));
  }
}

/// <summary>capture_app app identity: pid plus the optional display names.</summary>
public sealed record CaptureAppApp(int Pid, string? Name, string? Aumid, string? Exe)
{
  internal static CaptureAppApp? From(JsonElement el) => Wire.Int(el, "pid") is not { } pid
    ? null
    : new CaptureAppApp(pid, Wire.StrOr(el, "name"), Wire.StrOr(el, "aumid"), Wire.StrOr(el, "exe"));
}

/// <summary>capture_app window: title, broker window id, pixel bounds, and the modal
///     surface classification (kind: window|attached_dialog|open_panel|save_panel|popover).</summary>
#pragma warning disable CA1819 // Named decision: bounds is a wire row cell (int[4] x,y,w,h) copied verbatim into rendering; a copy per element allocates for nothing.
public sealed record CaptureAppWindow(string Title, int WindowId, int[] Bounds, string SurfaceKind, string SurfaceLifecycle)
#pragma warning restore CA1819
{
#pragma warning restore CA1819
  internal static CaptureAppWindow? From(JsonElement el)
  {
    string? title = Wire.Str(el, "title");
    int? windowId = Wire.Int(el, "window_id");
    int[]? bounds = Wire.Bounds(el);
    string? kind = Wire.Str(el, "surface_kind");
    string? lifecycle = Wire.Str(el, "surface_lifecycle");
    return title is null || windowId is null || bounds is null || kind is null || lifecycle is null
      ? null
      : new CaptureAppWindow(title, windowId.Value, bounds, kind, lifecycle);
  }
}

/// <summary>One observed element row: the spec 3.1 element table, wire-faithful.
///     There is NO default_action flag on the wire: the renderer derives it from
///     actions[] containing press/invoke (the ubiquitous default press).</summary>
#pragma warning disable CA1819 // Named decision: bounds is a wire row cell, see CaptureAppWindow.
public sealed record CaptureAppElement(
  int Index,
  string Role,
  string Kind,
  string? Title,
  string? Value,
  int[] Bounds,
  bool Enabled,
  bool Editable,
  IReadOnlyList<string> Actions,
  bool Focused,
  bool Selected,
  bool Pressable,
  bool HasMenu,
  int? ChildrenTotal,
  int? ChildrenShown,
  int? ChildrenOffset)
{
  internal static CaptureAppElement? From(JsonElement el)
  {
    if (el.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    int? index = Wire.Int(el, "index");
    string? role = Wire.Str(el, "role");
    string? kind = Wire.Str(el, "kind");
    int[]? bounds = Wire.Bounds(el);
    if (index is null || role is null || kind is null || bounds is null)
    {
      return null;
    }

    List<string> actions = [];
    if (el.TryGetProperty("actions", out JsonElement actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement a in actionsEl.EnumerateArray())
      {
        if (a.ValueKind == JsonValueKind.String)
        {
          actions.Add(a.GetString()!);
        }
      }
    }

    return new CaptureAppElement(index.Value, role, kind, Wire.StrOr(el, "title"), Wire.StrOr(el, "value"),
      bounds, Wire.Bool(el, "enabled"), Wire.Bool(el, "editable"), actions, Wire.Bool(el, "focused"),
      Wire.Bool(el, "selected"), Wire.Bool(el, "pressable"), Wire.Bool(el, "has_menu"),
      Wire.Int(el, "children_total"), Wire.Int(el, "children_shown"), Wire.Int(el, "children_offset"));
  }
}

/// <summary>capture_app screenshot: base64 PNG raster, pixel size, the pointer cell, and
///     the blank flag (non-actionable rasters are withheld upstream, not delivered).</summary>
public sealed record CaptureAppScreenshot(string Data, int Width, int Height, CaptureAppPointer? PointerRect, bool Blank)
{
  internal static CaptureAppScreenshot? From(JsonElement el)
  {
    string? data = Wire.Str(el, "data");
    int? width = Wire.Int(el, "width");
    int? height = Wire.Int(el, "height");
    if (data is null || width is null || height is null)
    {
      return null;
    }

    CaptureAppPointer? pointer = null;
    if (el.TryGetProperty("pointer", out JsonElement pointerEl) && pointerEl.ValueKind == JsonValueKind.Object)
    {
      pointer = CaptureAppPointer.From(pointerEl);
      if (pointer is null)
      {
        return null;
      }
    }

    return new CaptureAppScreenshot(data, width.Value, height.Value, pointer, Wire.Bool(el, "blank"));
  }
}

/// <summary>The pointer overlay cell: global rect of the pointer marker.</summary>
public sealed record CaptureAppPointer(int X, int Y, int W, int H)
{
  internal static CaptureAppPointer? From(JsonElement el) => Wire.Int(el, "x") is not { } x
    || Wire.Int(el, "y") is not { } y || Wire.Int(el, "w") is not { } w || Wire.Int(el, "h") is not { } h
    ? null
    : new CaptureAppPointer(x, y, w, h);
}

/// <summary>Action receipt (spec 7): delivery-state honesty on the wire. action_sent=false
///     must survive to the surface exactly as the broker reported it.</summary>
public sealed record BrokerActionReceipt(bool ActionSent, string DispatchStatus, string? EffectEvidence)
{
  /// <summary>Accepted dispatch states per spec 7.</summary>
  public const string Accepted = "accepted";
  public const string PossiblySent = "possibly_sent";

  internal static BrokerActionReceipt? From(JsonElement el)
  {
    if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("action_sent", out JsonElement sentEl)
        || (sentEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
    {
      return null;
    }

    string? dispatch = Wire.Str(el, "dispatch_status");
    return dispatch is not (Accepted or PossiblySent)
      ? null
      : new BrokerActionReceipt(sentEl.ValueKind == JsonValueKind.True, dispatch, Wire.StrOr(el, "effect_evidence"));
  }

  /// <summary>Parses the reply's result as an action receipt; null when the result is absent
  ///     or not a receipt shape.</summary>
  public static BrokerActionReceipt? From(BrokerReply reply)
  {
    ArgumentNullException.ThrowIfNull(reply);
    return reply.Error is not null || reply.Result is not { } el ? null : From(el);
  }
}

/// <summary>Wire-field extraction helpers shared by the typed records: strict kinds,
///     null on kind mismatch (the caller decides fail-closed).</summary>
internal static class Wire
{
  public static string? Str(JsonElement el, string name) => el.TryGetProperty(name, out JsonElement e)
    && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

  public static string? StrOr(JsonElement el, string name) => Str(el, name);

  public static int? Int(JsonElement el, string name) => el.TryGetProperty(name, out JsonElement e)
    && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;



  public static bool Bool(JsonElement el, string name) => el.TryGetProperty(name, out JsonElement e)
    && e.ValueKind == JsonValueKind.True;

  public static int[]? Bounds(JsonElement el) => el.TryGetProperty("bounds", out JsonElement e)
    && e.ValueKind == JsonValueKind.Array
    ? [.. e.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : throw new FormatException("bounds element"))]
    : null;
}
