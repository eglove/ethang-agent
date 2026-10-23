using Avalonia.Headless.XUnit;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>A tool result's images must survive the host wire and reach the transcript
///     (issue #230). Every conversation runs in a host process, so an image the payload
///     dropped could never be rendered - the generated-image artifact and the computer
///     screenshot both went dark for exactly that reason.</summary>
public class ToolResultWireImageTests
{
  // A real 1x1 PNG: the transcript decodes the bytes into a bitmap, so the fixture
  // must actually decode (a signature-only payload proves the wire, not the renderer).
  private const string PngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  [Fact]
  public void Parse_ToolResultPayloadWithImages_CarriesThem()
  {
    string json = RootTurnToolResultPayload.Serialize("image_generation", "ok", null,
        ".ethang/images/a.png", ".ethang/images/a.png",
        [new RootTurnImage("image/png", PngBase64)]);

    RootTurnToolPayload? parsed = RootTurnToolPayload.Parse(json);

    Assert.NotNull(parsed);
    RootTurnImage image = Assert.Single(parsed.Images!);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(PngBase64, image.Base64Data);
  }

  [Fact]
  public void Parse_ToolResultPayloadWithoutImages_YieldsNoImages()
  {
    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", "content");

    RootTurnToolPayload? parsed = RootTurnToolPayload.Parse(json);

    Assert.NotNull(parsed);
    Assert.True(parsed.Images is null || parsed.Images.Count == 0);
  }

  // AvaloniaFact: the transcript DECODES the bytes into a bitmap, which needs an
  // Avalonia platform (the same requirement the computer-screenshot render test has).
  [AvaloniaFact]
  public async Task SessionVm_ToolResultEventWithImages_RendersThemOnTheEntry()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();

    await vm.ApplyUiStreamEventAsync(new UiStreamEvent.ToolResultEvent("image_generation",
        ".ethang/images/a.png", ".ethang/images/a.png", false, "image_generation",
        [new ToolResultImage("image/png", PngBase64)])).ConfigureAwait(true);

    ToolResultEntry entry = Assert.IsType<ToolResultEntry>(
        Assert.Single(vm.Transcript.Entries.OfType<ToolResultEntry>()));
    _ = Assert.Single(entry.Images!);
  }
}
