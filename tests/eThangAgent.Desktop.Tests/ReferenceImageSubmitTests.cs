using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.FileSystem.ACL;
using eThangAgent.TestSupport;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>A message that names a file the app cannot show as lines still sends: the harness
///     names the path and the model decides what to do with it, so nothing is decoded and
///     nothing is refused for being unreadable. These drive the transcript, so they need the
///     headless UI thread and carry the process trait with it.</summary>
[Trait("Category", "Process")]
public sealed class ReferenceImageSubmitTests
{
  [AvaloniaFact]
  public async Task SubmitAsync_ImageReference_NamesThePathAndSendsNoBytes()
  {
    // A referenced picture is a POINTER like any other reference: the app does not read it,
    // so no image rides the turn and the model reads it with read_image if it wants it.
    string root = TestTempRoot.CreateDirectory("ethang-refimage").FullName;
    try
    {
      await File.WriteAllBytesAsync(Path.Combine(root, "shot.png"), [1, 2, 3],
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(root));
      FakeHostSession session = await FakeHostSession
          .StartAsync(["done"], references: new ReferenceResolver(null, root))
          .ConfigureAwait(true);

      await session.ViewModel.SubmitAsync("what is @shot.png").ConfigureAwait(true);

      RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
      Assert.Empty(request.PreTurn);
      Assert.Contains("@shot.png", request.Input, StringComparison.Ordinal);
      Assert.Contains("[references]", request.Input, StringComparison.Ordinal);
      Assert.DoesNotContain(session.ViewModel.Transcript.Entries, e => e is ToolResultEntry);
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
  public async Task SubmitAsync_UnreadableReference_IsNamedLikeAnyOtherPath()
  {
    // An archive is a path like any other now: nothing tries to open it, so nothing refuses
    // it, and the model learns what it is when it chooses to look.
    string root = TestTempRoot.CreateDirectory("ethang-refzip").FullName;
    try
    {
      await File.WriteAllBytesAsync(Path.Combine(root, "bundle.zip"), [0x50, 0x4B, 0x03, 0x04],
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      using DirectFileSystemAccess files = new(new EditorConfigTextConventionPolicy(root));
      FakeHostSession session = await FakeHostSession
          .StartAsync(["done"], references: new ReferenceResolver(null, root))
          .ConfigureAwait(true);

      await session.ViewModel.SubmitAsync("look at @bundle.zip").ConfigureAwait(true);

      Assert.DoesNotContain(session.ViewModel.Transcript.Entries, e => e is NoticeEntry);
      RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
      Assert.Empty(request.PreTurn);
      Assert.StartsWith("look at @bundle.zip", request.Input, StringComparison.Ordinal);
      Assert.Contains("bundle.zip", request.Input, StringComparison.Ordinal);
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
