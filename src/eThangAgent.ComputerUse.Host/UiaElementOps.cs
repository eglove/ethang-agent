using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Patterns;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Real UIA element operations (task 18, R1): the dispatcher's element path
///     resolves the target from the broker's last-walk element cache and performs the
///     REAL pattern operation (Value/Invoke/SetFocus) through FlaUI. Every unsupported
///     pattern is an honest typed error - not_settable / not_selectable /
///     action_unavailable - never a faked receipt. Resolution failure answers
///     element_unavailable.</summary>
public sealed class UiaElementOps(Func<int, AutomationElement?> resolver) : IElementBoundsResolver, IElementActionSink
{
  private readonly Func<int, AutomationElement?> _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

  /// <summary>C3: resolves the element's bounds center through the UIA cache for the drag
  ///     path. False when the element is absent from the cache or its rectangle is empty
  ///     (the honest failure the dispatcher surfaces).</summary>
  public bool TryResolveBoundsCenter(int element, out int centerX, out int centerY)
  {
    centerX = 0;
    centerY = 0;
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return false;
    }

    try
    {
      Rectangle bounds = target.BoundingRectangle;
      if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
      {
        return false;
      }

      centerX = bounds.X + (bounds.Width / 2);
      centerY = bounds.Y + (bounds.Height / 2);
      return true;
    }
    catch (ElementNotAvailableException)
    {
      return false;
    }
  }

  /// <summary>Fix round 5 (F1): the element click - UIA Invoke when the element
  ///     advertises it, Toggle as the honest pressable alternative, else the typed
  ///     action_unavailable. Never a coordinate click at guessed bounds.</summary>
  public BrokerResponse Invoke(int element)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    try
    {
      if (target.Patterns.Invoke.TryGetPattern(out IInvokePattern? invoke))
      {
        invoke.Invoke();
        return BrokerResponse.Accepted();
      }

      if (target.Patterns.Toggle.TryGetPattern(out ITogglePattern? toggle))
      {
        toggle.Toggle();
        return BrokerResponse.Accepted();
      }

      return BrokerResponse.Fail("action_unavailable",
        $"element {element} advertises no invoke/toggle pattern, so it cannot be clicked; nothing ran. " +
        "Reach it another way: click a coordinate at its bounds centre, or focus it with Tab and press Return.");
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (FlaUIException ex)
    {
      return BrokerResponse.Fail("action_unavailable", $"element {element} rejected the invoke (UIA {ex.HResult}); nothing ran.");
    }
  }

  /// <summary>Fix round 5 (F3): the element scroll - the UIA ScrollPattern when the
  ///     element supports it (per-page Scroll with the direction's sign), else the
  ///     honest action_unavailable.</summary>
  public BrokerResponse Scroll(int element, string direction, int pages)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    if (!target.Patterns.Scroll.TryGetPattern(out IScrollPattern? scroll))
    {
      return BrokerResponse.Fail("action_unavailable",
        $"element {element} exposes no UIA Scroll pattern; nothing scrolled. " +
        "Scroll the window instead with a coordinate target over the region, or focus a control inside it and press PageDown.");
    }

    try
    {
      ScrollAmount vertical = direction == "up" ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement;
      for (int page = 0; page < Math.Max(1, pages); page++)
      {
        scroll.Scroll(ScrollAmount.NoAmount, vertical);
      }

      return BrokerResponse.Accepted();
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (FlaUIException ex)
    {
      return BrokerResponse.Fail("action_unavailable", $"element {element} rejected the scroll (UIA {ex.HResult}); nothing scrolled.");
    }
    catch (InvalidOperationException ex)
    {
      return BrokerResponse.Fail("action_unavailable", $"element {element} cannot scroll in that direction: {ex.Message}");
    }
  }

  public BrokerResponse Focus(int element)
  {
    AutomationElement? target = Resolve(element);
    return target is null ? ElementUnavailable(element) : TrySetFocus(target, element);
  }

  /// <summary>Sets the element's value and then READS IT BACK: the Value pattern accepts
  ///     a write without applying it for controls that render their own text (native
  ///     spinners, segmented date fields, some combo boxes), so a receipt that only
  ///     reports the write was sent claims an effect nobody observed. A value that did
  ///     not take is the honest not_settable refusal naming what the element holds.</summary>
  public BrokerResponse SetValue(int element, string text)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    if (!target.Patterns.Value.TryGetPattern(out IValuePattern? valuePattern))
    {
      return BrokerResponse.Fail("not_settable", $"element {element} does not support the UIA Value pattern; nothing was changed.");
    }

    try
    {
      valuePattern.SetValue(text);
      return VerifyValue(element, valuePattern, text);
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (InvalidOperationException ex)
    {
      return BrokerResponse.Fail("not_settable", $"element {element} rejected the value: {ex.Message}");
    }
    catch (FlaUIException ex)
    {
      return BrokerResponse.Fail("not_settable", $"element {element} rejected the value (UIA {ex.HResult}).");
    }
  }

  /// <summary>Reads the value back after a write. A value the element did not take is a
  ///     typed refusal naming what it holds instead of a receipt for an effect that did
  ///     not happen; an element that vanished mid-write is the honest unavailable.</summary>
  private static BrokerResponse VerifyValue(int element, IValuePattern valuePattern, string written)
  {
    string? actual;
    try
    {
      actual = valuePattern.Value.ValueOrDefault;
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }

    return string.Equals(actual, written, StringComparison.Ordinal)
      ? BrokerResponse.Accepted()
      : BrokerResponse.Fail("not_settable",
        $"element {element} accepted the write but still reads '{actual ?? "(no value)"}': the control does not apply a Value write (native spinners and segmented fields render their own text). " +
        "Nothing was changed; focus the field and type into it instead.",
        "action_sent=false");
  }

  public BrokerResponse PerformAction(int element, string actionName)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    // I7: membership-checked honest dispatch - ONLY run the pattern the element actually
    // supports AND the model requested. No fallback substitution: a named action the element
    // does not advertise fails action_unavailable naming the action, never a wrong receipt.
    try
    {
      return RunAction(target, element, actionName);
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (FlaUIException ex)
    {
      return BrokerResponse.Fail("action_unavailable",
        $"element {element} rejected action '{actionName}' (UIA {ex.HResult}); nothing ran.");
    }
  }

  /// <summary>The pattern dispatch: only run the pattern the element actually supports
  ///     AND the model requested; an unadvertised action is the typed refusal.</summary>
  private static BrokerResponse RunAction(AutomationElement target, int element, string actionName)
  {
    switch (actionName)
    {
      case "press" or "invoke" or "click" when target.Patterns.Invoke.TryGetPattern(out IInvokePattern? invoke):
        invoke.Invoke();
        return BrokerResponse.Accepted();

      case "toggle" or "press" or "click" when target.Patterns.Toggle.TryGetPattern(out ITogglePattern? toggle):
        toggle.Toggle();
        return BrokerResponse.Accepted();

      case "expand" or "collapse" or "open" when target.Patterns.ExpandCollapse.TryGetPattern(out IExpandCollapsePattern? expandCollapse):
        return RunExpandCollapse(expandCollapse, actionName);

      default:
        return BrokerResponse.Fail("action_unavailable",
          $"element {element} does not support the requested action '{actionName}'; nothing ran.");
    }
  }

  private static BrokerResponse RunExpandCollapse(IExpandCollapsePattern expandCollapse, string actionName)
  {
    if (actionName == "collapse")
    {
      expandCollapse.Collapse();
    }
    else
    {
      expandCollapse.Expand();
    }

    return BrokerResponse.Accepted();
  }

  public BrokerResponse SelectText(int element)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    if (!target.Patterns.Text.IsSupported)
    {
      return BrokerResponse.Fail("not_selectable", $"element {element} exposes no UIA Text pattern; no selection was made.");
    }

    // The UIA Text pattern exposes no client-side selection shaping: its ranges are
    // read-only over this surface. Honest refusal.
    return BrokerResponse.Fail("not_selectable", $"element {element}'s text pattern is read-only over UIA; selection is unavailable.");
  }

  private AutomationElement? Resolve(int element) => _resolver(element);

  private static BrokerResponse ElementUnavailable(int element) =>
    BrokerResponse.Fail("element_unavailable", $"element {element} is not in the broker's latest observation; observe first.");

  private static BrokerResponse TrySetFocus(AutomationElement target, int element)
  {
    try
    {
      target.Focus();
      WaitFocusLands(target);
      return BrokerResponse.Accepted();
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (FlaUIException ex)
    {
      return BrokerResponse.Fail("internal", $"SetFocus failed (UIA {ex.HResult}).", "action_sent=false");
    }
  }

  /// <summary>UIA SetFocus returns before the Win32 focus state (GetGUIThreadInfo)
  ///     reflects the move - and targeted delivery reads exactly that state, so a
  ///     delivery racing the settle posts its characters to the stale focus target.
  ///     Event-driven settle (R5.1): a focus-changed event handler signals when the
  ///     element's native window becomes the focused window, bounded by a timeout -
  ///     best-effort: an element without a native handle, or a focus that never
  ///     lands within the bound, proceeds unchanged (delivery falls back to the
  ///     foreground window as before).</summary>
  private static void WaitFocusLands(AutomationElement target)
  {
    nint nativeWindow;
    try
    {
      nativeWindow = target.Properties.NativeWindowHandle.ValueOrDefault;
    }
    catch (FlaUIException)
    {
      return; // the element went stale between focus and read; nothing to settle
    }

    if (nativeWindow == 0)
    {
      return;
    }

    TaskCompletionSource landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FlaUI.Core.AutomationBase automation = target.Automation;
    FlaUI.Core.EventHandlers.FocusChangedEventHandlerBase handler = automation.RegisterFocusChangedEvent(OnFocusChanged);
    try
    {
      landed.Task.WaitAsync(FocusSettleBound, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (TimeoutException)
    {
      // The focus never landed within the bound: deliver as before.
    }
    finally
    {
      automation.UnregisterFocusChangedEvent(handler);
    }

    void OnFocusChanged(AutomationElement focused)
    {
      try
      {
        if (focused.Properties.NativeWindowHandle.ValueOrDefault == nativeWindow)
        {
          _ = landed.TrySetResult();
        }
      }
      catch (FlaUIException)
      {
        // A stale sender mid-handshake cannot signal the wait.
      }
    }
  }

  /// <summary>Upper bound on the post-focus settle wait.</summary>
  private static readonly TimeSpan FocusSettleBound = TimeSpan.FromMilliseconds(500);
}
