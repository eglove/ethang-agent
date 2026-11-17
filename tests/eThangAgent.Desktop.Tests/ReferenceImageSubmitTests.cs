using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.FileSystem.ACL;
using eThangAgent.TestSupport;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>A reference that reads a file the app cannot show as lines: a picture travels as
///     the bytes the read tool attached, and a file no read tool opens is refused by name while
///     the message still sends. These drive the transcript's picture rendering, so they need the
///     headless UI thread and carry the process trait with it.</summary>
[Trait("Category", "Process")]
public sealed class ReferenceImageSubmitTests
{
  /// <summary>A minimal valid 1x1 PNG: the transcript decodes the bitmap, so a fake header
  ///     would fail it.</summary>
  private static byte[] OnePixelPng() => Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

  [AvaloniaFact]
  public async Task SubmitAsync_ImageReference_CarriesThePictureToTheHost()
  {
    // A referenced picture is a picture the model must SEE: the app read it, so the bytes ride
    // the turn's pre-turn message rather than staying in this process.
    string root = TestTempRoot.CreateDirectory("ethang-refimage").FullName;
    try
    {
      await File.WriteAllBytesAsync(Path.Combine(root, "shot.png"), OnePixelPng(),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(root));
      StubReadTool image = ReferenceToolSurface.Image();
      FakeHostSession session = await FakeHostSession
          .StartAsync(["done"], references: new ReferenceInjector(files, null, root,
              tools: ReferenceToolSurface.Over(image)))
          .ConfigureAwait(true);

      await session.ViewModel.SubmitAsync("what is @shot.png").ConfigureAwait(true);

      RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
      RootTurnPreTurnMessage preTurn = Assert.Single(request.PreTurn);
      Assert.Equal("user", preTurn.Role, StringComparer.Ordinal);
      Assert.Contains("[read_image ", preTurn.Text, StringComparison.Ordinal);
      Assert.Contains("[image: image/png, 3 bytes]", preTurn.Text, StringComparison.Ordinal);
      RootTurnImage carried = Assert.Single(preTurn.Images);
      Assert.Equal("image/png", carried.MediaType, StringComparer.Ordinal);
      Assert.Equal(Convert.ToBase64String([1, 2, 3]), carried.Base64Data, StringComparer.Ordinal);
      // The transcript shows the picture the message carried, not just the annotation.
      ToolResultEntry card = Assert.Single(session.ViewModel.Transcript.Entries.OfType<ToolResultEntry>(),
          e => e.Name == "read_image");
      _ = Assert.Single(card.Images!);
    }
    finally
    {
#pragma warning disable CA1031 // Named decision: temp-dir cleanup is best effort.
      try
      {
        Directory.Delete(root, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a still-draining reader may pin the directory
      }
#pragma warning restore CA1031
    }
  }

  [Fact]
  public async Task SubmitAsync_UnreadableReference_PostsANoticeAndSendsTheMessage()
  {
    // An archive is not something a read tool opens, so the reference is refused by name and
    // the message still travels: the user asked for the message to be sent.
    string root = TestTempRoot.CreateDirectory("ethang-refzip").FullName;
    try
    {
      await File.WriteAllBytesAsync(Path.Combine(root, "bundle.zip"), [0x50, 0x4B, 0x03, 0x04],
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(root));
      FakeHostSession session = await FakeHostSession
          .StartAsync(["done"], references: new ReferenceInjector(files, null, root))
          .ConfigureAwait(true);

      await session.ViewModel.SubmitAsync("look at @bundle.zip").ConfigureAwait(true);

      Assert.Contains(session.ViewModel.Transcript.Entries,
          e => e is NoticeEntry notice && notice.Text.Contains("ReferenceUnreadable", StringComparison.Ordinal));
      RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
      Assert.Empty(request.PreTurn);
      Assert.Equal("look at @bundle.zip", request.Input, StringComparer.Ordinal);
    }
    finally
    {
#pragma warning disable CA1031 // Named decision: temp-dir cleanup is best effort.
      try
      {
        Directory.Delete(root, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a still-draining reader may pin the directory
      }
#pragma warning restore CA1031
    }
  }
}
