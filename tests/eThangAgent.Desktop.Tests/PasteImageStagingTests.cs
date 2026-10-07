using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Paste staging (issue #20): images attach to the view model, ride the next
///     turn's user message, and clear on submit. Format/size/batch violations are
///     notices, never throws.</summary>
public class PasteImageStagingTests
{
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
  private static readonly byte[] TinyPng = Convert.FromBase64String(TinyPngBase64);
  private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];

  private static AgentSessionViewModel MakeVm() => TestFixtures.CreateViewModel();

  [Fact]
  public void AttachImageBytes_Png_StagesPendingImage()
  {
    AgentSessionViewModel vm = MakeVm();
    Assert.True(vm.AttachImageBytes(TinyPng, "clipboard.png"));

    PendingImage pending = Assert.Single(vm.PendingImages);
    Assert.Equal("clipboard.png", pending.Label);
    Assert.Equal("image/png", pending.MediaType);
  }

  [Fact]
  public void AttachImageBytes_Jpeg_StagesWithImageJpeg()
  {
    AgentSessionViewModel vm = MakeVm();
    Assert.True(vm.AttachImageBytes(JpegBytes, "photo.jpg"));
    Assert.Equal("image/jpeg", Assert.Single(vm.PendingImages).MediaType);
  }

  [Fact]
  public void AttachImageBytes_NonImage_FailsWithNotice()
  {
    AgentSessionViewModel vm = MakeVm();
    Assert.False(vm.AttachImageBytes([0x47, 0x49, 0x46, 0x38], "anim.gif"));
    Assert.Empty(vm.PendingImages);
    Assert.Contains(vm.Transcript.Entries, e => e is NoticeEntry n && n.Text.Contains("image/png or image/jpeg", StringComparison.Ordinal));
  }

  [Fact]
  public void AttachImageBytes_Oversized_FailsWithNotice()
  {
    AgentSessionViewModel vm = MakeVm();
    byte[] big = new byte[ToolDomain.ImageLimits.MaxBytes + 1];
    Assert.False(vm.AttachImageBytes(big, "big.png"));
    Assert.Empty(vm.PendingImages);
  }

  [Fact]
  public void AttachImageBytes_FifthImage_FailsWithBatchNotice()
  {
    AgentSessionViewModel vm = MakeVm();
    for (int i = 0; i < ToolDomain.ImageLimits.MaxPerMessage; i++)
    {
      Assert.True(vm.AttachImageBytes(TinyPng, $"img{i}.png"));
    }

    Assert.False(vm.AttachImageBytes(TinyPng, "one-too-many.png"));
    Assert.Equal(ToolDomain.ImageLimits.MaxPerMessage, vm.PendingImages.Count);
  }

  [Fact]
  public void RemovePendingImage_RemovesTheIndexedEntry()
  {
    AgentSessionViewModel vm = MakeVm();
    _ = vm.AttachImageBytes(TinyPng, "a.png");
    _ = vm.AttachImageBytes(TinyPng, "b.png");
    vm.RemovePendingImage(0);
    Assert.Equal("b.png", Assert.Single(vm.PendingImages).Label);
  }

  [Fact]
  public async Task Submit_WithStagedImages_UserMessageCarriesImages_AndQueueClears()
  {
    AgentSessionViewModel vm = MakeVm();
    _ = vm.AttachImageBytes(TinyPng, "shot.png");

    await vm.SubmitAsync("what is this").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    UserMessageEntry entry = Assert.IsType<UserMessageEntry>(Assert.Single(
        vm.Transcript.Entries.OfType<UserMessageEntry>()));
    Assert.NotNull(entry.Images);
    Assert.Equal("shot.png", Assert.Single(entry.ImageLabels!));
    Assert.Empty(vm.PendingImages);
  }

  [Fact]
  public async Task Submit_WithoutImages_UserEntryHasNoImages()
  {
    AgentSessionViewModel vm = MakeVm();
    await vm.SubmitAsync("plain").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Null(Assert.IsType<UserMessageEntry>(Assert.Single(
        vm.Transcript.Entries.OfType<UserMessageEntry>())).Images);
  }
}
