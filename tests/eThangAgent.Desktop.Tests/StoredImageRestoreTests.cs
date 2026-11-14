using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>A restored transcript carries a stored image's PATH, never its bytes, so rendering the
///     picture means reading the file back through the same store that wrote it. These tests pin
///     the three outcomes that path can have: the file is there and the picture renders, the file is
///     gone and a notice stands in its place, and no store is wired at all (a bare transcript in a
///     test, a headless stub) - which is a notice naming the path rather than an exception or a
///     silently empty entry.</summary>
[Trait("Category", "Process")]
public class StoredImageRestoreTests
{
  // A real 1x1 PNG: the view-model decodes what it reads, so a signature-only fixture would not do.
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string StoredPath = @"C:\images\ethang-agent\shot.png";

  private static ConversationDomain.Message Restored(ConversationDomain.MessagePart part) =>
      new(ConversationDomain.Role.User, "what is this", T, Parts: [part]);

  [AvaloniaFact]
  public void Restore_StoredImagePart_ReadsTheFileAndRendersThePicture()
  {
    byte[] png = Convert.FromBase64String(TinyPngBase64);
    TranscriptViewModel vm = new(storedImageResolver: path =>
        path == StoredPath
            ? Result.Success(new StoredImageContent("image/png", png))
            : Result.Failure<StoredImageContent>(new DomainError("ImageNotFound", path)));

    vm.Restore([Restored(new ConversationDomain.MessagePart.ImageReferencePart(StoredPath, "image/png"))]);

    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(Assert.Single(vm.Entries.OfType<UserMessageEntry>()));
    _ = Assert.Single(entry.Images!);
    Assert.Empty(vm.Notices);
  }

  [Fact]
  public void Restore_StoredImageWhoseFileIsGone_RendersTheMessageAndANoticeNamingThePath()
  {
    TranscriptViewModel vm = new(storedImageResolver: path =>
        Result.Failure<StoredImageContent>(new DomainError("ImageNotFound", $"missing: {path}")));

    vm.Restore([Restored(new ConversationDomain.MessagePart.ImageReferencePart(StoredPath, "image/png"))]);

    // The message still renders; what is missing is the picture, and a notice names the file.
    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(Assert.Single(vm.Entries.OfType<UserMessageEntry>()));
    Assert.Equal("what is this", entry.Text);
    Assert.Null(entry.Images);
    Assert.Contains(vm.Notices, notice => notice.Contains(StoredPath, StringComparison.Ordinal));
  }

  [Fact]
  public void Restore_NoStoreWired_ReportsThePathInsteadOfThrowing()
  {
    TranscriptViewModel vm = new();

    vm.Restore([Restored(new ConversationDomain.MessagePart.ImageReferencePart(StoredPath, "image/png"))]);

    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(Assert.Single(vm.Entries.OfType<UserMessageEntry>()));
    Assert.Equal("what is this", entry.Text);
    Assert.Null(entry.Images);
    Assert.Contains(vm.Notices, notice => notice.Contains(StoredPath, StringComparison.Ordinal));
  }
}
