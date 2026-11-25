namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>The paging window over a walk: which slice travels, what the note discloses,
///     and that an out-of-range page answers honestly instead of erroring.</summary>
public class UiaWalkWindowTests
{
  [Fact]
  public void Select_WholeTreeFits_ReportsNothingToDisclose()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 12, offset: 0, limit: 400);
    Assert.Equal(0, window.Offset);
    Assert.Equal(12, window.Shown);
    Assert.False(window.HasMore);
    Assert.False(window.HasPrevious);
    Assert.Null(window.NextOffset);
    Assert.Null(window.Note());
  }

  [Fact]
  public void Select_FirstPageOfLargeTree_NamesTheNextOffset()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 1000, offset: 0, limit: 400);
    Assert.Equal(400, window.Shown);
    Assert.True(window.HasMore);
    Assert.Equal(400, window.NextOffset);
    string note = Assert.IsType<string>(window.Note());
    Assert.Contains("elements 0-399 of 1000", note, StringComparison.Ordinal);
    Assert.Contains("offset=400", note, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_MiddlePage_NamesBothNeighbours()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 1000, offset: 400, limit: 400);
    Assert.True(window.HasPrevious);
    Assert.True(window.HasMore);
    string note = Assert.IsType<string>(window.Note());
    Assert.Contains("offset=800", note, StringComparison.Ordinal);
    Assert.Contains("offset=0", note, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_LastPage_ReportsNoNextOffset()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 1000, offset: 800, limit: 400);
    Assert.Equal(200, window.Shown);
    Assert.False(window.HasMore);
    Assert.Null(window.NextOffset);
    string note = Assert.IsType<string>(window.Note());
    Assert.Contains("elements 800-999 of 1000", note, StringComparison.Ordinal);
    Assert.DoesNotContain("Next page", note, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_OffsetPastTheEnd_AnswersEmptyAtTheEnd_NotAnError()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 100, offset: 5000, limit: 400);
    Assert.Equal(100, window.Offset);
    Assert.Equal(0, window.Shown);
    Assert.False(window.HasMore);
    string note = Assert.IsType<string>(window.Note());
    Assert.Contains("no elements at offset 100", note, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_LimitAboveMaximum_IsClamped()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 5000, offset: 0, limit: 99999);
    Assert.Equal(UiaWalkWindow.MaxLimit, window.Shown);
  }

  [Fact]
  public void Note_TruncatedTree_SaysTheTotalIsAFloor()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 400, offset: 0, limit: 400) with { Truncated = true };
    string note = Assert.IsType<string>(window.Note());
    Assert.Contains("at least 400", note, StringComparison.Ordinal);
  }

  [Fact]
  public void Select_ZeroLimit_IsClampedToOne_RatherThanShowingNothing()
  {
    UiaWalkWindow window = UiaWalkWindow.Select(total: 10, offset: 0, limit: 0);
    Assert.Equal(1, window.Shown);
  }
}
