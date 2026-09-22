using System.Text.Json;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>The ACL adapter (task 18): implements the Tool Domain's
///     <see cref="IComputerAccess"/> over the supervised broker process. Commands
///     translate to broker methods (list_applications, list_windows, capture_app,
///     click, scroll, drag, type_text, press_key, hold_key, element_focus,
///     element_set_value, element_perform_action, element_select_text, element_press,
///     paste, stop_computer_control); the controller lease is taken lazily on the
///     first call that needs one and released by <see cref="ComputerCommand.Stop"/>.
///     Element targets resolve fail-closed through the <see cref="ObservationLedger"/>
///     (ELEMENT_UNAVAILABLE when the index is absent from the latest observation,
///     STALE_STATE when the window changed since the model last looked); coordinate
///     targets resolve through the <see cref="FrameRegistry"/> against the newest
///     delivered frame (STALE_STATE on any miss) and travel to the broker as resolved
///     global screen points. The CancellationToken flows into the pipe client.</summary>
public sealed class BrokerComputerAccess(BrokerSupervisor supervisor, bool ownsSupervisor = false) : IComputerAccess, IAsyncDisposable
{
  private readonly BrokerSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
  private readonly ObservationLedger _ledger = new();
  private readonly FrameRegistry _frames = new();
  private readonly bool _ownsSupervisor = ownsSupervisor;
  private bool _hasLease;

  /// <inheritdoc />
  public async Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    return command switch
    {
      ComputerCommand.ListApps => await RequestAsync("list_applications", null, ct).ConfigureAwait(false),
      ComputerCommand.ListWindows list => await RequestAsync("list_windows",
          JsonSerializer.SerializeToElement(new { app_ref = AppRef(list.App) }, BrokerWire.Options), ct)
        .ConfigureAwait(false),
      ComputerCommand.Observe observe => await ObserveAsync(observe, ct).ConfigureAwait(false),
      ComputerCommand.Stop => await StopAsync(ct).ConfigureAwait(false),
      _ => await ActionAsync(command, ct).ConfigureAwait(false),
    };
  }

  private async Task<ComputerOutcome> ObserveAsync(ComputerCommand.Observe command, CancellationToken ct)
  {
    JsonElement parameters = JsonSerializer.SerializeToElement(new
    {
      app_ref = AppRef(command.App),
      include_screenshot = command.IncludeScreenshot,
      snapshot_mode = command.DisableDiffing ? "force_full" : "auto",
    }, BrokerWire.Options);
    return await RequestAsync("capture_app", parameters, ct).ConfigureAwait(false);
  }

  private async Task<ComputerOutcome> StopAsync(CancellationToken ct)
  {
    if (!_hasLease)
    {
      // Nothing held: an honest no-op receipt (nothing was dispatched).
      return new ComputerOutcome.Receipt(ActionSent: false, BrokerActionReceipt.Accepted, null, null);
    }

    ComputerOutcome outcome = await RequestAsync("stop_computer_control", null, ct).ConfigureAwait(false);
    if (outcome is ComputerOutcome.Receipt)
    {
      _hasLease = false;
    }

    return outcome;
  }

  /// <summary>The input-path commands: resolve the target BEFORE any wire round trip
  ///     (a stale element or a dead frame never dispatches input), take the lease,
  ///     then dispatch the method with its parameters.</summary>
  private async Task<ComputerOutcome> ActionAsync(ComputerCommand command, CancellationToken ct)
  {
    (ComputerOutcome.Failure? failure, FrameCoordinateResult.Hit? primaryHit, FrameCoordinateResult.Hit? toHit) =
      ResolveTargets(command);
    if (failure is not null)
    {
      return failure;
    }

    ComputerOutcome? leaseFailure = await EnsureLeaseAsync(ct).ConfigureAwait(false);
    if (leaseFailure is not null)
    {
      return leaseFailure;
    }

    JsonElement parameters = ParametersFor(command, primaryHit, toHit);
    ComputerOutcome outcome = await RequestAsync(MethodFor(command), parameters, ct).ConfigureAwait(false);

    // I9: return_state=compact|full drives a post-action re-observe so the option is
    // never silently dropped. Failures of the re-observe ride the Receipt's TreeText slot.
    if (ReturnStateOf(command) is { } returnState && returnState != "none" && outcome is ComputerOutcome.Receipt receipt)
    {
      ComputerAppRef? app = AppOf(command);
      if (app is not null)
      {
        ComputerOutcome postState = await ObserveAsync(
            new ComputerCommand.Observe(app, IncludeScreenshot: false, DisableDiffing: false), ct).ConfigureAwait(false);
        if (postState is ComputerOutcome.Observation postObservation)
        {
          outcome = receipt with { TreeText = postObservation.TreeText };
        }
      }
    }

    return outcome;
  }


  private static string? ReturnStateOf(ComputerCommand command) => command switch
  {
    ComputerCommand.Click c => c.ReturnState,
    ComputerCommand.Drag d => d.ReturnState,
    ComputerCommand.Scroll s => s.ReturnState,
    ComputerCommand.TypeText t => t.ReturnState,
    ComputerCommand.SetValue v => v.ReturnState,
    ComputerCommand.SelectText s => s.ReturnState,
    ComputerCommand.Key k => k.ReturnState,
    ComputerCommand.Paste p => p.ReturnState,
    ComputerCommand.PerformAction a => a.ReturnState,
    _ => null,
  };

  private static ComputerTarget? TargetOf(ComputerCommand command) => command switch
  {
    ComputerCommand.Click c => c.Target,
    ComputerCommand.Scroll s => s.Target,
    ComputerCommand.SetValue v => v.Target,
    ComputerCommand.SelectText s => s.Target,
    ComputerCommand.PerformAction a => a.Target,
    ComputerCommand.Drag d => d.From,
    ComputerCommand.TypeText t => t.Target,
    ComputerCommand.Paste p => p.Target,
    _ => null,
  };

  /// <summary>Resolves every target the command carries (drag: from AND to) in ONE pass;
  ///     the returned hits travel to ParametersFor so coordinates resolve exactly once (I11).
  ///     Null failure means every present target resolved.</summary>
  private (ComputerOutcome.Failure? Failure, FrameCoordinateResult.Hit? PrimaryHit, FrameCoordinateResult.Hit? ToHit) ResolveTargets(
      ComputerCommand command)
  {
    ComputerTarget? primary = TargetOf(command);
    ComputerTarget? to = command is ComputerCommand.Drag d ? d.To : null;

    FrameCoordinateResult.Hit? primaryHit = null;
    FrameCoordinateResult.Hit? toHit = null;

    if (primary is { } p)
    {
      if (p.ElementIndex is { } index)
      {
        LedgerIndexCheck check = _ledger.ValidateIndex(LedgerKeyFor(AppOf(command)), index);
        if (!check.Ok)
        {
          return (new ComputerOutcome.Failure(
              check.Error ?? ComputerErrorCodes.ElementUnavailable,
              check.Message ?? "element target does not resolve; observe first."), null, null);
        }
      }
      else if (p.X is { } px && p.Y is { } py)
      {
        if (_frames.ResolveForCoordinate(FrameRegistry.LatestToken, px, py) is not FrameCoordinateResult.Hit ph)
        {
          return (new ComputerOutcome.Failure(
              ComputerErrorCodes.StaleState,
              "the coordinate target does not resolve against a delivered screenshot frame; observe with include_screenshot first."), null, null);
        }

        primaryHit = ph;
      }
      else
      {
        return (new ComputerOutcome.Failure(
            ComputerErrorCodes.InvalidApp, "target carries neither element index nor coordinates."), null, null);
      }
    }

    if (to is { } toTarget)
    {
      if (toTarget.ElementIndex is { } toIndex)
      {
        LedgerIndexCheck toCheck = _ledger.ValidateIndex(LedgerKeyFor(AppOf(command)), toIndex);
        if (!toCheck.Ok)
        {
          return (new ComputerOutcome.Failure(
              toCheck.Error ?? ComputerErrorCodes.ElementUnavailable,
              toCheck.Message ?? "drag 'to' element target does not resolve; observe first."), null, null);
        }
      }
      else if (toTarget.X is { } tx && toTarget.Y is { } ty)
      {
        if (_frames.ResolveForCoordinate(FrameRegistry.LatestToken, tx, ty) is not FrameCoordinateResult.Hit th)
        {
          return (new ComputerOutcome.Failure(
              ComputerErrorCodes.StaleState,
              "the drag 'to' coordinate does not resolve against a delivered screenshot frame; observe with include_screenshot first."), null, null);
        }

        toHit = th;
      }
      else
      {
        return (new ComputerOutcome.Failure(
            ComputerErrorCodes.InvalidApp, "drag 'to' carries neither element index nor coordinates."), null, null);
      }
    }

    return (null, primaryHit, toHit);
  }

  private static ComputerAppRef? AppOf(ComputerCommand command) => command switch
  {
    ComputerCommand.Click c => c.App,
    ComputerCommand.Drag d => d.App,
    ComputerCommand.Scroll s => s.App,
    ComputerCommand.TypeText t => t.App,
    ComputerCommand.SetValue v => v.App,
    ComputerCommand.SelectText s => s.App,
    ComputerCommand.Key k => k.App,
    ComputerCommand.Paste p => p.App,
    ComputerCommand.PerformAction a => a.App,
    _ => null,
  };

  private static string MethodFor(ComputerCommand command) => command switch
  {
    ComputerCommand.Click => "click",
    ComputerCommand.Drag => "drag",
    ComputerCommand.Scroll => "scroll",
    ComputerCommand.TypeText => "type_text",
    ComputerCommand.SetValue => "element_set_value",
    ComputerCommand.SelectText => "element_select_text",
    ComputerCommand.Key k => k.HoldSeconds is { } hold && hold > 0 ? "hold_key" : "press_key",
    ComputerCommand.Paste => "paste",
    ComputerCommand.PerformAction => "element_perform_action",
    _ => throw new InvalidOperationException("unreachable: ActionAsync only sees input commands"),
  };

  private static JsonElement ParametersFor(ComputerCommand command,
      FrameCoordinateResult.Hit? primaryHit, FrameCoordinateResult.Hit? toHit)
  {
    Dictionary<string, object?> map = command switch
    {
      ComputerCommand.Click c => new()
      {
        ["mouse_button"] = c.MouseButton,
        ["click_count"] = c.ClickCount,
        ["modifiers"] = c.Modifiers,
        ["strategy"] = c.Strategy,
      },
      ComputerCommand.Drag d => new() { ["modifiers"] = d.Modifiers },
      ComputerCommand.Scroll s => new()
      {
        ["scroll_direction"] = s.ScrollDirection,
        ["scroll_amount"] = s.ScrollAmount,
        ["strategy"] = s.Strategy,
      },
      ComputerCommand.TypeText t => new() { ["text"] = t.Text, ["strategy"] = t.Strategy },
      ComputerCommand.SetValue v => new() { ["value"] = v.Value, ["strategy"] = v.Strategy },
      ComputerCommand.SelectText s => new()
      {
        ["text"] = s.Text,
        ["prefix"] = s.Prefix,
        ["suffix"] = s.Suffix,
        ["selection_type"] = s.SelectionType,
      },
      ComputerCommand.Key k => new()
      {
        ["key"] = k.Text,
        ["repeat"] = k.Repeat,
        ["hold_seconds"] = k.HoldSeconds,
        ["strategy"] = k.Strategy,
      },
      ComputerCommand.Paste p => new() { ["text"] = p.Text, ["format"] = p.Format },
      ComputerCommand.PerformAction a => new() { ["action_name"] = a.Action },
      _ => [],
    };

    // The primary target resolves client-side once (I11): the broker receives the
    // resolved global screen point or the observation element index.
    if (TargetOf(command) is { } primaryTarget)
    {
      if (primaryTarget.ElementIndex is { } index)
      {
        map["element"] = index;
      }
      else if (primaryHit is { } hit)
      {
        map["x"] = (int)hit.Resolution.X;
        map["y"] = (int)hit.Resolution.Y;
      }
    }

    // C3: drag carries BOTH endpoints - the 'to' target resolves identically.
    if (command is ComputerCommand.Drag drag)
    {
      if (drag.To.ElementIndex is { } toIndex)
      {
        map["to_element"] = toIndex;
      }
      else if (toHit is { } toPoint)
      {
        map["to_x"] = (int)toPoint.Resolution.X;
        map["to_y"] = (int)toPoint.Resolution.Y;
      }
    }

    if (AppOf(command) is { } app)
    {
      map["app_ref"] = AppRef(app);
    }

    return JsonSerializer.SerializeToElement(map, BrokerWire.Options);
  }

  private Task<ComputerOutcome?> EnsureLeaseAsync(CancellationToken ct) =>
    _hasLease ? Task.FromResult<ComputerOutcome?>(null) : TakeOverAsync(ct);

  private async Task<ComputerOutcome?> TakeOverAsync(CancellationToken ct)
  {
    ComputerOutcome outcome = await RequestAsync("controller_takeover", null, ct).ConfigureAwait(false);
    if (outcome is ComputerOutcome.Failure failure)
    {
      return failure;
    }

    _hasLease = true;
    return null;
  }

  /// <summary>One broker round trip: errors map through BrokerErrorMapper, action
  ///     receipts survive verbatim, and capture_app results parse into an
  ///     Observation (tree text via TreeTextRenderer, ledger + frame registration,
  ///     screenshot re-encoded through ScreenshotEncoder). List results render as an
  ///     Observation whose tree text IS the JSON rows (they carry no element table).</summary>
  private async Task<ComputerOutcome> RequestAsync(string method, JsonElement? parameters, CancellationToken ct)
  {
    BrokerReply reply;
    try
    {
      reply = await _supervisor.RequestEnvelopeAsync(method, parameters, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      // Cancellation honesty: the caller stopped waiting, so delivery is UNKNOWN. A typed
      // retryable TIMEOUT names the cancellation - never a silent clean result.
      return new ComputerOutcome.Failure(ComputerErrorCodes.Timeout,
          "the call was cancelled before the broker replied; whether the action landed is unknown.");
    }

    catch (BrokerEnvelopeException envelope)
    {
      // C4: transport failures arrive as typed envelope exceptions. HELPER_UNAVAILABLE carries
      // the retry hint; the budget code maps to TIMEOUT. Exceptions never escape the adapter.
      string code = envelope.Code == ComputerErrorCodes.Timeout
        ? ComputerErrorCodes.Timeout
        : ComputerErrorCodes.HelperUnavailable;
      string hint = code == ComputerErrorCodes.Timeout
        ? "the broker call exceeded its budget; whether the action landed is unknown."
        : "the broker is unavailable; the pipe may be restarting - retry the request.";
      return new ComputerOutcome.Failure(code, envelope.Message + " " + hint);
    }
    if (reply.Error is { } error)
    {
      return BrokerErrorMapper.Map(error.Code, error.Message);
    }

    if (method == "capture_app")
    {
      return CaptureToObservation(reply);
    }

    if (BrokerActionReceipt.From(reply) is { } receipt)
    {
      return new ComputerOutcome.Receipt(receipt.ActionSent, receipt.DispatchStatus, receipt.EffectEvidence, null);
    }

    if (reply.Result is { } result)
    {
      // list_applications / list_windows / controller results: no element table exists,
      // so the observation's tree text carries the JSON rows verbatim.
      return new ComputerOutcome.Observation(result.GetRawText(), "s-list", [], null, null, null);
    }

    return new ComputerOutcome.Failure(ComputerErrorCodes.Internal, "broker returned an unrecognized result shape.");
  }

  /// <summary>Parses one capture_app reply into the model-facing Observation: tree text
  ///     rendered through the renderer, the state registered in the ledger, a delivered
  ///     actionable raster registered as a coordinate frame, and the screenshot
  ///     re-encoded for the model (withheld when the broker marked it blank).</summary>
  internal ComputerOutcome CaptureToObservation(BrokerReply reply)
  {
    if (CaptureAppResult.From(reply) is not { } capture)
    {
      // I10: an unparseable capture is a typed retryable failure - never an empty success.
      return new ComputerOutcome.Failure(
          ComputerErrorCodes.StaleState,
          "the broker's capture could not be parsed; observe again.");
    }


    bool treeShown = capture.SnapshotMode != "no_change";
    _lastObservedPid = capture.App.Pid;
    string stateId = _ledger.Record(

        new LedgerKey(capture.App.Pid, capture.Window.WindowId),
        new LedgerWindow(capture.Window.Title, capture.Window.Bounds[0], capture.Window.Bounds[1],
            capture.Window.Bounds[2], capture.Window.Bounds[3]),
        capture.Elements, treeShownToModel: treeShown, screenshotOnly: false);
    _ = stateId;

    ToolResultImage? screenshot = null;
    string? withheld = null;
    ComputerFrameRef? frame = null;
    if (capture.Screenshot is { } shot)
    {
      if (shot.Blank)
      {
        withheld = "the window's raster is uniform (no actionable content)";
      }
      else
      {
        byte[] png = Convert.FromBase64String(shot.Data);
        DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);
        screenshot = new ToolResultImage("image/jpeg", delivered.Base64);
        ScreenshotWindowRect rect = new(capture.Window.Bounds[0], capture.Window.Bounds[1],
            capture.Window.Bounds[2], capture.Window.Bounds[3]);
        frame = _frames.Add(delivered.Width, delivered.Height, rect, actionable: true);
      }
    }

    string treeText = TreeTextRenderer.Render(capture);
    IReadOnlyList<ComputerElement> elements = [.. capture.Elements.Select(e => new ComputerElement(
        e.Index, e.Role, e.Kind, e.Title, e.Value, e.Bounds, e.Enabled, e.Editable, e.Actions,
        e.Focused, e.Selected, e.Pressable, e.HasMenu, e.ChildrenTotal, e.ChildrenShown, e.ChildrenOffset))];
    return new ComputerOutcome.Observation(treeText, stateId, elements, frame, withheld, screenshot);
  }

  /// <summary>M14: for name/aumid refs the broker resolves the pid; the ledger key derives
  ///     from the pid of the LAST observed capture (fail-closed stays: no observation means
  ///     pid 0, and ValidateIndex answers ELEMENT_UNAVAILABLE).</summary>
  private LedgerKey LedgerKeyFor(ComputerAppRef? app)
  {
    // M14: explicit pid wins; name/aumid refs fall back to the last observed capture pid.
    if (app is { } reference && reference.Pid is { } explicitPid)
    {
      return new LedgerKey(explicitPid, reference.WindowId ?? 0);
    }

#pragma warning disable IDE0046 // Named decision: two typed returns read clearer than nested ternaries here.
    if (_lastObservedPid is not { } observedPid)
    {
      return new LedgerKey(0, 0);
    }

    return new LedgerKey(observedPid, 0);
  }
#pragma warning restore IDE0046 // Named decision: two typed returns read clearer than nested ternaries here.

  private int? _lastObservedPid;

  private static object AppRef(ComputerAppRef app) => new
  {
    name = app.Name,
    pid = app.Pid,
    aumid = app.Aumid,
    window_id = app.WindowId,
  };

  /// <summary>Disposes the owned supervisor when this access owns it. When the caller
  ///     owns the supervisor (composition shares one broker per workspace), disposal is
  ///     the caller's concern and this is a no-op.</summary>
  public async ValueTask DisposeAsync()
  {
    if (_ownsSupervisor)
    {
      await _supervisor.DisposeAsync().ConfigureAwait(false);
    }
  }
}
