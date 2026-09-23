using System.Text.Json;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The clipboard seam (task 17): save/set/read/restore against the real
///     Win32 clipboard in production; recorded by the test double.</summary>
public interface IClipboardAccess
{
  string? Save();
  bool SetText(string text);
  bool Restore(string? previousContent);
}

/// <summary>The paste keystroke seam: production sends Ctrl+V via SendInput.</summary>
public interface IPasteKeyDispatch
{
  void SendCtrlV();
}

/// <summary>Test double: no-op key dispatch.</summary>
public sealed class FakeKeyDispatch : IPasteKeyDispatch
{
  public static readonly FakeKeyDispatch Noop = new();
  public void SendCtrlV() { }
}

/// <summary>The atomic clipboard paste (task 17): ONE broker-side operation that
///     saves the previous clipboard, sets the new text (CF_UNICODETEXT), sends Ctrl+V,
///     waits for a consumption signal (clipboard sequence number change or a short
///     settle), and restores the saved content. A paste no app consumed within the
///     window is a timeout error - and the restore STILL happens. Whether a
///     background app may be pasted into is the caller's frontmost decision
///     (foreground_required), not this class's.</summary>
public sealed class ClipboardPaster(IClipboardAccess clipboard, IPasteKeyDispatch keys, Func<bool>? consumptionSignal = null)
{
  /// <summary>The consumption wait: brief settle windows (production polls the
  ///     clipboard sequence number; the seam collapses it to a consumed flag).</summary>
  public const int ConsumeWaitMilliseconds = 250;

  public BrokerResponse Paste(InputOperation operation, JsonElement parameters)
  {
    _ = operation;
    if (parameters.ValueKind != JsonValueKind.Object
        || !parameters.TryGetProperty("text", out JsonElement textEl)
        || textEl.ValueKind != JsonValueKind.String)
    {
      return BrokerResponse.Fail("invalid_request", "paste requires params.text (string).");
    }

    string? saved = clipboard.Save();
    string text = textEl.GetString()!;
    try
    {
      if (!clipboard.SetText(text))
      {
        return BrokerResponse.Fail("internal", "clipboard set failed; nothing was dispatched.");
      }

      keys.SendCtrlV();
      return WaitConsumed()
        ? BrokerResponse.Ok(JsonSerializer.SerializeToElement(new
        {
          action_sent = true,
          dispatch_status = BrokerReceipt.Accepted,
        }))
        : BrokerResponse.Fail(
          "timeout",
          "no app consumed the paste within the window; the previous clipboard content was restored.");
    }
    finally
    {
      _ = clipboard.Restore(saved);
    }
  }

  /// <summary>The consumption signal seam: production polls the clipboard sequence
  ///     number for a change (or settles briefly); the injected signal is authoritative.
  ///     No signal means consumed (the happy path completes immediately).</summary>
  /// <summary>Polls the consumption signal within the wait window (fix round I5): the
  ///     production signal is the clipboard sequence number; the seam answer is
  ///     authoritative per poll. Poll interval is a quarter of the window.</summary>
  private bool WaitConsumed()
  {
    if (consumptionSignal is null)
    {
      return true; // no signal wired: the happy path completes immediately
    }

    int interval = Math.Max(1, ConsumeWaitMilliseconds / 4);
    for (int elapsed = 0; elapsed < ConsumeWaitMilliseconds; elapsed += interval)
    {
      if (consumptionSignal())
      {
        return true;
      }

      Thread.Sleep(interval);
    }

    return consumptionSignal();
  }
};
