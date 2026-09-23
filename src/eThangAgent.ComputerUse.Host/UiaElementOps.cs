using System.Windows.Automation;

namespace eThangAgent.ComputerUse.Host;

/// <summary>Real UIA element operations (task 18, R1): the dispatcher's element path
///     resolves the target from the broker's last-walk element cache and performs the
///     REAL pattern operation (Value/Invoke/SetFocus). Every unsupported pattern is an
///     honest typed error - not_settable / not_selectable / action_unavailable - never
///     a faked receipt. Resolution failure answers element_unavailable.</summary>
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
      System.Windows.Rect bounds = target.Current.BoundingRectangle;
      if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
      {
        return false;
      }

      centerX = (int)(bounds.X + (bounds.Width / 2));
      centerY = (int)(bounds.Y + (bounds.Height / 2));
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
      if (target.TryGetCurrentPattern(InvokePattern.Pattern, out object? invokePattern) && invokePattern is InvokePattern invoke)
      {
        invoke.Invoke();
        return BrokerResponse.Accepted();
      }

      if (target.TryGetCurrentPattern(TogglePattern.Pattern, out object? togglePattern) && togglePattern is TogglePattern toggle)
      {
        toggle.Toggle();
        return BrokerResponse.Accepted();
      }

      return BrokerResponse.Fail("action_unavailable",
        $"element {element} advertises no invoke/toggle pattern; nothing ran.");
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
      return BrokerResponse.Fail("action_unavailable", $"element {element} rejected the invoke (COM {ex.HResult}); nothing ran.");
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

    if (!target.TryGetCurrentPattern(ScrollPattern.Pattern, out object? pattern)
      || pattern is not ScrollPattern scroll)
    {
      return BrokerResponse.Fail("action_unavailable",
        $"element {element} exposes no UIA Scroll pattern; nothing scrolled.");
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
    catch (System.Runtime.InteropServices.COMException ex)
    {
      return BrokerResponse.Fail("action_unavailable", $"element {element} rejected the scroll (COM {ex.HResult}); nothing scrolled.");
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

  public BrokerResponse SetValue(int element, string text)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) || pattern is not ValuePattern valuePattern)
    {
      return BrokerResponse.Fail("not_settable", $"element {element} does not support the UIA Value pattern; nothing was changed.");
    }

    try
    {
      valuePattern.SetValue(text);
      return BrokerResponse.Accepted();
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (InvalidOperationException ex)
    {
      return BrokerResponse.Fail("not_settable", $"element {element} rejected the value: {ex.Message}");
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
      return BrokerResponse.Fail("not_settable", $"element {element} rejected the value (COM {ex.HResult}).");
    }
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
      switch (actionName)
      {
        case "press" or "invoke" or "click" when target.TryGetCurrentPattern(InvokePattern.Pattern, out object? invokePattern) && invokePattern is InvokePattern invoke:
          invoke.Invoke();
          return BrokerResponse.Accepted();

        case "toggle" or "press" or "click" when target.TryGetCurrentPattern(TogglePattern.Pattern, out object? togglePattern) && togglePattern is TogglePattern toggle:
          toggle.Toggle();
          return BrokerResponse.Accepted();

        case "expand" or "collapse" or "open" when target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expandPattern) && expandPattern is ExpandCollapsePattern expandCollapse:
          if (actionName == "collapse")
          {
            expandCollapse.Collapse();
          }
          else
          {
            expandCollapse.Expand();
          }
          return BrokerResponse.Accepted();

        default:
          return BrokerResponse.Fail("action_unavailable",
            $"element {element} does not support the requested action '{actionName}'; nothing ran.");
      }
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
      return BrokerResponse.Fail("action_unavailable",
        $"element {element} rejected action '{actionName}' (COM {ex.HResult}); nothing ran.");
    }
  }
  public BrokerResponse SelectText(int element)
  {
    AutomationElement? target = Resolve(element);
    if (target is null)
    {
      return ElementUnavailable(element);
    }

    if (!target.TryGetCurrentPattern(TextPattern.Pattern, out _))
    {
      return BrokerResponse.Fail("not_selectable", $"element {element} exposes no UIA Text pattern; no selection was made.");
    }

    // The UIA Text pattern is read-only on the client side: selection shaping needs the
    // pattern's range APIs that the client surface does not expose. Honest refusal.
    return BrokerResponse.Fail("not_selectable", $"element {element}'s text pattern is read-only over UIA; selection is unavailable.");
  }

  private AutomationElement? Resolve(int element) => _resolver(element);

  private static BrokerResponse ElementUnavailable(int element) =>
    BrokerResponse.Fail("element_unavailable", $"element {element} is not in the broker's latest observation; observe first.");

  private static BrokerResponse TrySetFocus(AutomationElement target, int element)
  {
    try
    {
      target.SetFocus();
      return BrokerResponse.Accepted();
    }
    catch (ElementNotAvailableException)
    {
      return ElementUnavailable(element);
    }
    catch (System.Runtime.InteropServices.COMException ex)
    {
      return BrokerResponse.Fail("internal", $"SetFocus failed (COM {ex.HResult}).", "action_sent=false");
    }
  }
}
