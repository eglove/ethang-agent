using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>ClipboardPaster structure tests (task 17): the paste sequence must SAVE the
///     previous clipboard, set the new text, send Ctrl+V, wait for a consumption signal,
///     then RESTORE the saved content; a paste no app consumed within the window is a
///     timeout error. The native clipboard is abstracted behind a seam; the sequence
///     is asserted against a recording double.</summary>
public class ClipboardPasterTests
{
  [Fact]
  public void Paste_SavesSetsSendsWaitsRestores_InOrder()
  {
    RecordingClipboard clipboard = new(consumed: true);
    ClipboardPaster paster = new(clipboard, new RecordingKeyDispatch(clipboard), WaitJournal(clipboard));
    using InputOperation operation = CreateOperation();
    BrokerResponse response = paster.Paste(operation, JsonDocument.Parse("""{"text":"hello"}""").RootElement);
    Assert.Null(response.Error);
    Assert.Equal(
      ["save", "set", "ctrl+v", "wait", "restore"],
      clipboard.Journal);
  }

  [Fact]
  public void Paste_NotConsumed_IsTimeoutError_AndStillRestores()
  {
    RecordingClipboard clipboard = new(consumed: false);
    ClipboardPaster paster = new(clipboard, new RecordingKeyDispatch(clipboard), WaitJournal(clipboard));
    using InputOperation operation = CreateOperation();
    BrokerResponse response = paster.Paste(operation, JsonDocument.Parse("""{"text":"hello"}""").RootElement);
    _ = Assert.NotNull(response.Error);
    Assert.Equal("timeout", response.Error.Value.Code);
    Assert.Equal("restore", clipboard.Journal[^1]);
  }

  [Fact]
  public void Paste_SavedContent_RestoredVerbatim()
  {
    RecordingClipboard clipboard = new(consumed: true) { SavedContent = "previous" };
    ClipboardPaster paster = new(clipboard, new RecordingKeyDispatch(clipboard), WaitJournal(clipboard));
    using InputOperation operation = CreateOperation();
    _ = paster.Paste(operation, JsonDocument.Parse("""{"text":"hello"}""").RootElement);
    Assert.Equal("previous", clipboard.LastRestored);
  }

  [Fact]
  public void Paste_MissingText_IsInvalidRequest()
  {
    RecordingClipboard clipboard = new(consumed: true);
    ClipboardPaster paster = new(clipboard, new RecordingKeyDispatch(clipboard), WaitJournal(clipboard));
    using InputOperation operation = CreateOperation();
    BrokerResponse response = paster.Paste(operation, JsonDocument.Parse("""{}""").RootElement);
    _ = Assert.NotNull(response.Error);
    Assert.Equal("invalid_request", response.Error.Value.Code);
    Assert.Empty(clipboard.Journal);
  }

  /// <summary>Test double: journals the keystroke into the SAME journal the clipboard
  ///     records into, so the full paste sequence is asserted in order.</summary>
  internal sealed class RecordingKeyDispatch(RecordingClipboard clipboard) : IPasteKeyDispatch
  {
    public void SendCtrlV() => clipboard.Journal.Add("ctrl+v");
  }

  private static Func<bool> WaitJournal(RecordingClipboard clipboard) =>
    () =>
    {
      clipboard.Journal.Add("wait");
      return clipboard.Consumed;
    };

  private static InputOperation CreateOperation()
  {
    InputSerializer serializer = new();
    return serializer.TryBegin("paste")!;
  }
};

/// <summary>Test double: records the operation journal for sequence assertions.</summary>
internal sealed class RecordingClipboard(bool consumed) : IClipboardAccess
{
  public List<string> Journal { get; } = [];
  public string? SavedContent { get; init; }
  public string? LastRestored { get; private set; }
  public bool Consumed { get; } = consumed;

  public string? Save()
  {
    Journal.Add("save");
    return SavedContent;
  }

  public bool SetText(string text)
  {
    Journal.Add("set");
    return true;
  }

  public bool Restore(string? previousContent)
  {
    Journal.Add("restore");
    LastRestored = previousContent;
    return true;
  }
}
