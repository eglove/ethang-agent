using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The input-method router (task 17): maps the wire method names onto the
///     native surfaces. Receipt semantics are honored everywhere: action_sent=true only
///     after dispatch actually happened; dispatch_status=accepted on the unit path;
///     timeouts AFTER a write are possibly_sent; a rejection BEFORE any dispatch carries
///     action_sent=false in details. perform_action's element-action key is action_name
///     (JSON duplicate-key constraint) and is mirrored exactly here.</summary>
internal static class InputRouter
{
  /// <summary>One receipt: the dispatch happened and the broker vouches for it.</summary>
  internal static BrokerResponse Accepted() => BrokerResponse.Ok(
    JsonSerializer.SerializeToElement(new { action_sent = true, dispatch_status = BrokerReceipt.Accepted }));

  /// <summary>A timeout AFTER the write: the action may have landed - honesty first.</summary>
  internal static BrokerResponse PossiblySent(string what) => BrokerResponse.Ok(
    JsonSerializer.SerializeToElement(new { action_sent = true, dispatch_status = BrokerReceipt.PossiblySent, effect_evidence = what }));

  public static BrokerResponse Execute(int id, string method, JsonElement? parameters)
  {
    _ = id;
    return method switch
    {
      "paste" => Paste(parameters),
      "press_key" or "hold_key" or "type_text" or "click" or "scroll" or "drag"
        or "element_focus" or "element_set_value" or "element_perform_action"
        or "element_select_text" or "element_press" => Keyed(method, parameters),
      _ => BrokerResponse.Fail("method_not_found", $"unknown input method: {method}."),
    };
  }

  private static BrokerResponse Paste(JsonElement? parameters)
  {
    if (parameters is not { } p || !p.TryGetProperty("text", out JsonElement textEl) || textEl.ValueKind != JsonValueKind.String)
    {
      return BrokerResponse.Fail("invalid_request", "paste requires params.text (string).");
    }

    // The broker-side atomic paste: save/set/Ctrl+V/wait/restore with the timeout
    // contract inside ClipboardPaster. The foreground refusal is the caller's
    // (strategy=event) decision, not the paster's.
    InputSerializer serializer = new();
    using InputOperation? operation = serializer.TryBegin("paste");
    ClipboardPaster paster = new(new NativeClipboard(), new NativeKeyDispatch());
    return paster.Paste(operation!, JsonSerializer.SerializeToElement(new { text = textEl.GetString() }));
  }

  /// <summary>perform_action mirrors the element-action key action_name exactly (the JSON
  ///     duplicate-key constraint from the surface's parsing contract).</summary>
  private static bool RequiresActionName(JsonElement? parameters) =>
    parameters is not { } p
    || !p.TryGetProperty("action_name", out JsonElement actionEl)
    || actionEl.ValueKind != JsonValueKind.String;

  private static BrokerResponse Keyed(string method, JsonElement? parameters)
  {
    // Key-bearing methods parse their chord typed: an unknown key answers
    // invalid_request and names the key (the model self-corrects).
    if (parameters is { } p && p.TryGetProperty("key", out JsonElement keyEl) && keyEl.ValueKind == JsonValueKind.String)
    {
      try
      {
        _ = KeyChordNormalizer.Parse(keyEl.GetString()!);
      }
      catch (KeyChordException ex)
      {
        return BrokerResponse.Fail("invalid_request", ex.Message);
      }
    }

    // perform_action mirrors the element-action key action_name exactly (the JSON
    // duplicate-key constraint from the surface's parsing contract).

    return method == "element_perform_action" && RequiresActionName(parameters)
      ? BrokerResponse.Fail("invalid_request", "element_perform_action requires params.action_name (string).")
      : InputDispatch.Create().Dispatch(method, parameters);
  }
};

/// <summary>The native Win32 clipboard (task 17): OpenClipboard/empty/CF_UNICODETEXT
///     set/restore against the real clipboard. Best-effort by contract: clipboard
///     failures surface as internal errors from the paster, never crashes.</summary>
internal sealed class NativeClipboard : IClipboardAccess
{
  // Task 18 integration hardens the retry loop for clipboard-lock contention;
  // reading the current text is best-effort here.
  public string? Save() => Clipboard.GetText();

  public bool SetText(string text)
  {
    Clipboard.SetText(text);
    return true;
  }

  public bool Restore(string? previousContent)
  {
    if (string.IsNullOrEmpty(previousContent))
    {
      return true; // nothing worth restoring (or the previous content was empty)
    }

    Clipboard.SetText(previousContent);
    return true;
  }
};

/// <summary>The native paste keystroke: Ctrl+V via SendInput.</summary>
internal sealed class NativeKeyDispatch : IPasteKeyDispatch
{
  public void SendCtrlV() => _ = NativeInput.SendChord(new KeyChord(0x56, KeyModifiers.Control));
};
