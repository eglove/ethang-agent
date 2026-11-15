using System.Text;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>A tool result whose text was shortened, or whose images were dropped, for the
///     wire budget must still reach the window (R3): the app reads the result shape through
///     <see cref="RootTurnToolPayload.Parse"/>, which refuses a payload it cannot decode or
///     whose status token it does not know.</summary>
public class ToolPayloadBudgetWireTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  [Fact]
  public void Parse_FittingPayload_RendersNameStatusSummaryContentAndImages()
  {
    string json = RootTurnToolResultPayload.Serialize("read", "ok", "main.cs", "ok", "content",
        [new RootTurnImage("image/png", PngBase64)]);

    RootTurnToolPayload parsed = Assert.IsType<RootTurnToolPayload>(RootTurnToolPayload.Parse(json));

    Assert.True(parsed.IsResult);
    Assert.False(parsed.IsError);
    Assert.Equal("read", parsed.Name);
    Assert.Equal("main.cs", parsed.Title);
    Assert.Equal("content", parsed.Content);
    _ = Assert.Single(parsed.Images!);
  }

  [Fact]
  public void Parse_TruncatedContent_StillRendersTheResult()
  {
    string content = new('x', 300_000);

    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", content);

    Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, FramedPayloadBudget.MaxPayloadBytes);
    RootTurnToolPayload parsed = Assert.IsType<RootTurnToolPayload>(RootTurnToolPayload.Parse(json));
    Assert.True(parsed.IsResult);
    Assert.Equal("read", parsed.Name);
    Assert.Contains("truncated", parsed.Content, StringComparison.Ordinal);
    Assert.Contains("300000", parsed.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_DroppedImage_StatesItInTheSummaryAndCarriesNoImages()
  {
    string json = RootTurnToolResultPayload.Serialize("image_generation", "ok", null,
        "ok", "ten bytes!", [new RootTurnImage("image/png", new string('A', 5 * 1024 * 1024))]);

    RootTurnToolPayload parsed = Assert.IsType<RootTurnToolPayload>(RootTurnToolPayload.Parse(json));

    Assert.True(parsed.Images is null || parsed.Images.Count == 0);
    Assert.Equal("ten bytes!", parsed.Content);
    Assert.Contains("1 image not shown: too large to preview here", parsed.Summary,
        StringComparison.Ordinal);
  }

  [Fact]
  public void Parse_ErrorResultWithTruncatedContent_KeepsTheErrorFlag()
  {
    string json = RootTurnToolResultPayload.Serialize("exec", "error", null, "exit 1",
        new string('e', 300_000));

    RootTurnToolPayload parsed = Assert.IsType<RootTurnToolPayload>(RootTurnToolPayload.Parse(json));

    Assert.True(parsed.IsResult);
    Assert.True(parsed.IsError);
    Assert.Contains("truncated", parsed.Content, StringComparison.Ordinal);
  }
}
