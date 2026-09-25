using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The input-method helpers (fix round M9): perform_action mirrors the
///     element-action key action_name exactly (the JSON duplicate-key constraint from the
///     surface's parsing contract), and paste validates its text before the broker-held
///     operation flows into the atomic ClipboardPaster. The real dispatch is
///     InputDispatch.Dispatch on the wired path; the native clipboard and Ctrl+V live here.
///     No local serializer: the broker's serializer owns the operation.</summary>
internal static class InputRouter
{
  /// <summary>The app_ref pid a paste targets (-1 when none). The paste gate reads
  ///     it BEFORE the clipboard is touched (fix round 5, F6).</summary>
  public static int TargetPid(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("app_ref", out JsonElement appRef) && appRef.ValueKind == JsonValueKind.Object
      && appRef.TryGetProperty("pid", out JsonElement pidEl) && pidEl.ValueKind == JsonValueKind.Number
      && pidEl.TryGetInt32(out int pid)
        ? pid
        : -1;

  /// <summary>The element index a paste targets (-1 when the target is not an
  ///     element). Element-targeted pastes focus the element after the gate (F8) -
  ///     the gate itself applies to every paste.</summary>
  public static int ElementIndex(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("element", out JsonElement el)
      && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int index) ? index : -1;

  /// <summary>perform_action requires params.action_name (string).</summary>
  public static bool RequiresActionName(JsonElement? parameters) =>
    parameters is not { } p
    || !p.TryGetProperty("action_name", out JsonElement actionEl)
    || actionEl.ValueKind != JsonValueKind.String;

  /// <summary>Paste validation: text is required; the paster does the rest.</summary>
  public static BrokerResponse PasteFailure() =>
    BrokerResponse.Fail("invalid_request", "paste requires params.text (string).");

  /// <summary>Paste on the wired path: the gate and the element-focus step live in the
  ///     broker's paste routing (F8); the operation from the broker-held serializer
  ///     flows into the atomic ClipboardPaster. No local serializer here (M9).</summary>
  public static BrokerResponse Paste(InputOperation operation, JsonElement? parameters)
  {
    if (parameters is not { } p || !p.TryGetProperty("text", out JsonElement textEl) || textEl.ValueKind != JsonValueKind.String)
    {
      return PasteFailure();
    }

    ClipboardPaster paster = new(new NativeClipboard(), new NativeKeyDispatch());
    return paster.Paste(operation, JsonSerializer.SerializeToElement(new { text = textEl.GetString() }));
  }
};

/// <summary>The native Win32 clipboard (task 17): best-effort by contract - clipboard
///     failures surface as internal errors from the paster, never crashes. SEAM CHOICE
///     (controller ruling, accepted): IClipboardAccess/ IPasteKeyDispatch keep the paster
///     unit-testable; the real clipboard and Ctrl+V chord live in these two classes.</summary>
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

/// <summary>The native paste keystroke: Ctrl+V via SendInput (real dispatch, M6).</summary>
internal sealed class NativeKeyDispatch : IPasteKeyDispatch
{
  public void SendCtrlV() => _ = NativeInput.SendChord(new KeyChord(0x56, KeyModifiers.Control));
};
