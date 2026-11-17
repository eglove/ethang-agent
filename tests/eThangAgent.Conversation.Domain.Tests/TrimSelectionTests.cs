using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Conversation.Domain.Tests;

public class TrimSelectionTests
{
  [Fact]
  public void Empty_MeansTheDefault()
  {
    TrimSelection selection = TrimSelection.Parse(null).Value!;
    Assert.Null(selection.LastCount);
    Assert.Null(selection.Ordinals);
  }

  [Fact]
  public void SingleOrdinal_Parses() =>
      Assert.Equal([3], TrimSelection.Parse("r3").Value!.Ordinals);

  [Fact]
  public void CommaList_ParsesAscendingDistinct() =>
      Assert.Equal([3, 7, 12], TrimSelection.Parse("r7,r3,r12,r3").Value!.Ordinals);

  [Fact]
  public void Range_ExpandsInclusive() =>
      Assert.Equal([3, 4, 5], TrimSelection.Parse("r3-r5").Value!.Ordinals);

  [Fact]
  public void LastN_Parses()
  {
    TrimSelection selection = TrimSelection.Parse("last 4").Value!;
    Assert.Equal(4, selection.LastCount);
    Assert.Null(selection.Ordinals);
  }

  [Fact]
  public void BareNumber_IsRejected_SoAMessagePositionCanNeverTrimTheWrongResult()
  {
    Result<TrimSelection> parsed = TrimSelection.Parse("3");
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidSelection", parsed.Error.Code);
  }

  [Fact]
  public void PositionRangeSyntax_IsRejected() =>
      Assert.False(TrimSelection.Parse("3..7").IsSuccess);

  [Fact]
  public void ReversedRange_IsRejected() =>
      Assert.False(TrimSelection.Parse("r7-r3").IsSuccess);

  [Fact]
  public void ZeroOrdinal_IsRejected() =>
      Assert.False(TrimSelection.Parse("r0").IsSuccess);

  [Fact]
  public void LastZero_IsRejected() =>
      Assert.False(TrimSelection.Parse("last 0").IsSuccess);

  [Fact]
  public void Render_RoundTripsEachForm()
  {
    Assert.Equal("last 2", TrimSelection.Parse("last 2").Value!.Render());
    Assert.Equal("r3,r7", TrimSelection.Parse("r3,r7").Value!.Render());
  }
}
