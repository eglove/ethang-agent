using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Resume renders user-message image parts (issue #20): a persisted paste
///     re-decodes on restore; a corrupt payload degrades to a notice, never throws.</summary>
public class UserImageRestoreTests
{
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  [AvaloniaFact]
  public void Restore_UserMessageWithImagePart_RendersImageStrip()
  {
    TranscriptViewModel vm = new();
    vm.Restore(
    [
      new ConversationDomain.Message(ConversationDomain.Role.User, "what is this",
          DateTimeOffset.UtcNow,
          Parts: [new ConversationDomain.MessagePart.ImagePart("image/png", TinyPngBase64)]),
    ]);

    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(Assert.Single(vm.Entries));
    Assert.NotNull(entry.Images);
    _ = Assert.Single(entry.Images);
  }

  [Fact]
  public void Restore_UserMessageWithoutParts_LegacyShape()
  {
    TranscriptViewModel vm = new();
    vm.Restore([new ConversationDomain.Message(ConversationDomain.Role.User, "plain", DateTimeOffset.UtcNow)]);
    Assert.Null(Assert.IsType<UserMessageEntry>(Assert.Single(vm.Entries)).Images);
  }

  /// <summary>The no-crash contract (issue #20): a payload the platform decoder
  ///     cannot interpret must never break the restore - the user entry renders
  ///     regardless. (Whether the decoder throws or yields garbage is platform
  ///     behavior; the degradation notice is pinned on the live stream path, where
  ///     the I13 contract lives.)</summary>
  [AvaloniaFact]
  public void Restore_UndecodablePayload_NeverBreaksRestore()
  {
    TranscriptViewModel vm = new();
    vm.Restore(
    [
      new ConversationDomain.Message(ConversationDomain.Role.User, "look",
          DateTimeOffset.UtcNow,
          Parts: [new ConversationDomain.MessagePart.ImagePart("image/png", "bm90LWFuLWltYWdl")]),
    ]);

    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(vm.Entries.OfType<UserMessageEntry>().First());
    Assert.Equal("look", entry.Text);
  }
}
