using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the pinned PDF page-range grammar (task 1):
///     1-based, comma-separated segments 'N', 'N-M', and the open tail 'N-'. Every
///     rejection carries the exact pinned code (InvalidPageRange) and a message
///     naming the offending segment.</summary>
public class PdfPageRangeTests
{
  [Theory]
  [InlineData("3", 3, null)]
  [InlineData("2-5", 2, 5)]
  [InlineData("8-", 8, null)]
  public void Parse_Accepts_Valid_Grammar(string text, int start, int? end)
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse(text);
    Assert.True(parsed.IsSuccess);
    PdfPageSegment segment = Assert.Single(parsed.Value.Segments);
    Assert.Equal(start, segment.Start);
    Assert.Equal(end, segment.End);
  }

  [Fact]
  public void Parse_Accepts_Mixed_Segments_In_Order()
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse("1-3,5,8-");
    Assert.True(parsed.IsSuccess);
    Assert.Equal(3, parsed.Value.Segments.Count);
    Assert.Equal(new PdfPageSegment(1, 3), parsed.Value.Segments[0]);
    Assert.Equal(new PdfPageSegment(5, null), parsed.Value.Segments[1]);
    Assert.Equal(new PdfPageSegment(8, null), parsed.Value.Segments[2]);
  }

  [Fact]
  public void Parse_Tolerates_Whitespace()
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse(" 1-3 , 5 ");
    Assert.True(parsed.IsSuccess);
    Assert.Equal(2, parsed.Value.Segments.Count);
    Assert.Equal(new PdfPageSegment(1, 3), parsed.Value.Segments[0]);
    Assert.Equal(new PdfPageSegment(5, null), parsed.Value.Segments[1]);
  }

  [Theory]
  [InlineData("")]
  [InlineData("0")]
  [InlineData("-3")]
  [InlineData("5-2")]
  [InlineData("1-3,2-4")]
  [InlineData("1,,2")]
  [InlineData("x")]
  public void Parse_Rejects_Invalid_Grammar(string text)
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse(text);
    Assert.False(parsed.IsSuccess);
    Assert.Equal(PdfErrorCodes.InvalidPageRange, parsed.Error.Code);
  }

  [Fact]
  public void Parse_Rejects_Segment_After_Open_Tail()
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse("8-,10");
    Assert.False(parsed.IsSuccess);
    Assert.Equal(PdfErrorCodes.InvalidPageRange, parsed.Error.Code);
  }
}
