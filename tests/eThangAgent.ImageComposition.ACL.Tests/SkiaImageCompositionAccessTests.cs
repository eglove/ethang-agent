using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using SkiaSharp;

namespace eThangAgent.ImageComposition.ACL.Tests;

/// <summary>The renderer draws what the scene says: the geometry lands as pixels, the colours are
///     the ones asked for, and the canvas is the size the caller stated.
///     <para>THE ASSERTIONS READ PIXELS, not just the byte count. A renderer that produced a
///     correctly-sized blank PNG would satisfy 'it made an image' while drawing nothing at all,
///     which is exactly the failure mode a composed-image tool must not have.</para></summary>
public class SkiaImageCompositionAccessTests
{
  private static readonly SceneColor Ink = SceneColor.Parse("#E65A3C").Value!;
  private static readonly SceneColor Paper = SceneColor.Parse("#0C0E14").Value!;

  [Fact]
  public async Task Render_DrawsTheSceneAtTheStatedSize()
  {
    SceneImage scene = SceneImage.Create(320, 200, Paper, [
        new SceneShape.Rectangle(new SceneRect(40, 40, 120, 80), Ink, null),
    ]).Value!;

    Result<ComposedImage> rendered = await new SkiaImageCompositionAccess()
        .RenderAsync(scene, TestContext.Current.CancellationToken);

    Assert.True(rendered.IsSuccess, rendered.Error?.Message);
    Assert.Equal(320, rendered.Value.Width);
    Assert.Equal(200, rendered.Value.Height);
    Assert.True(rendered.Value.PngBytes.Length > 0);
    // A real PNG: the 8-byte signature is the file's own claim about what it is.
    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
        rendered.Value.PngBytes[..8]);
  }

  [Fact]
  public async Task Render_TheShapeIsActuallyPaintedWhereItWasAskedFor()
  {
    SceneImage scene = SceneImage.Create(100, 100, Paper, [
        new SceneShape.Rectangle(new SceneRect(0, 0, 50, 50), Ink, null),
    ]).Value!;

    Result<ComposedImage> rendered = await new SkiaImageCompositionAccess()
        .RenderAsync(scene, TestContext.Current.CancellationToken);
    Assert.True(rendered.IsSuccess, rendered.Error?.Message);

    using SKBitmap bitmap = SKBitmap.Decode(rendered.Value.PngBytes)
        ?? throw new InvalidOperationException("the renderer's bytes are not a decodable PNG");
    SKColor inside = bitmap.GetPixel(10, 10);
    SKColor outside = bitmap.GetPixel(90, 90);
    Assert.Equal(Ink.Red, inside.Red);
    Assert.Equal(Ink.Green, inside.Green);
    Assert.Equal(Ink.Blue, inside.Blue);
    Assert.Equal(Paper.Red, outside.Red);
    Assert.Equal(Paper.Green, outside.Green);
    Assert.Equal(Paper.Blue, outside.Blue);
  }

  [Fact]
  public async Task Render_TextIsDrawnRatherThanSkipped()
  {
    // Text is the primitive with a dependency outside the process (an installed face), so it is
    // the one most likely to be silently dropped. A canvas with text must differ from the same
    // canvas without it.
    SceneColor white = SceneColor.Parse("#FFFFFF").Value!;
    SceneImage blank = SceneImage.Create(400, 120, Paper, []).Value!;
    SceneImage withText = SceneImage.Create(400, 120, Paper, [
        new SceneShape.TextRun("local render works", 20, 40, 40, null, white),
    ]).Value!;

    Result<ComposedImage> blankRender = await new SkiaImageCompositionAccess()
        .RenderAsync(blank, TestContext.Current.CancellationToken);
    Result<ComposedImage> textRender = await new SkiaImageCompositionAccess()
        .RenderAsync(withText, TestContext.Current.CancellationToken);
    Assert.True(blankRender.IsSuccess, blankRender.Error?.Message);
    Assert.True(textRender.IsSuccess, textRender.Error?.Message);

    Assert.NotEqual(blankRender.Value.PngBytes.Length, textRender.Value.PngBytes.Length);
    using SKBitmap bitmap = SKBitmap.Decode(textRender.Value.PngBytes)
        ?? throw new InvalidOperationException("the renderer's bytes are not a decodable PNG");
    int whitePixels = 0;
    for (int y = 0; y < bitmap.Height; y++)
    {
      for (int x = 0; x < bitmap.Width; x++)
      {
        if (bitmap.GetPixel(x, y).Red > 200 && bitmap.GetPixel(x, y).Green > 200)
        {
          whitePixels++;
        }
      }
    }
    Assert.True(whitePixels > 50, $"text drew only {whitePixels} light pixels - it was skipped");
  }

  [Fact]
  public async Task Render_APolylineFillsItsEnclosedArea()
  {
    SceneImage scene = SceneImage.Create(100, 100, Paper, [
        new SceneShape.Polyline(
            [new ScenePoint(10, 10), new ScenePoint(90, 10), new ScenePoint(50, 90)],
            Closed: true, Fill: Ink, Stroke: null),
    ]).Value!;

    Result<ComposedImage> rendered = await new SkiaImageCompositionAccess()
        .RenderAsync(scene, TestContext.Current.CancellationToken);
    Assert.True(rendered.IsSuccess, rendered.Error?.Message);

    using SKBitmap bitmap = SKBitmap.Decode(rendered.Value.PngBytes)
        ?? throw new InvalidOperationException("the renderer's bytes are not a decodable PNG");
    // Inside the triangle: centred under the apex.
    Assert.Equal(Ink.Red, bitmap.GetPixel(50, 40).Red);
    // The top-right corner is outside the triangle and keeps the background.
    Assert.Equal(Paper.Red, bitmap.GetPixel(95, 5).Red);
  }
}
