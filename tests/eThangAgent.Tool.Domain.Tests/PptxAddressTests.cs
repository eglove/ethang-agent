using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the pinned pptx address grammar: every address form
///     round-trips through the parser and back to text unchanged, every malformed form
///     returns an InvalidAddress domain error rather than throwing, and the record's
///     programmer-error guards reject an address that cannot exist.</summary>
public class PptxAddressTests
{
  // ---- Accepted shapes and their canonical text ----

  [Theory]
  [InlineData("s1")]
  [InlineData("s3")]
  [InlineData("s41")]
  [InlineData("s3.sh2")]
  [InlineData("s3.sh1.sh1")]
  [InlineData("s3.sh2.sh1.sh4")]
  [InlineData("s3.sh2.t1.c4")]
  [InlineData("s1.sh1.t2.c3")]
  [InlineData("s3.sh2.sh1.t1.c1")]
  [InlineData("s3.notes")]
  [InlineData("s3.anim1")]
  [InlineData("s12.anim27")]
  public void Parse_AcceptedForm_RoundTripsThroughRender(string raw)
  {
    Result<PptxAddress> parsed = PptxAddress.Parse(raw);
    Assert.True(parsed.IsSuccess);
    Assert.Equal(raw, parsed.Value.Render());
  }

  [Fact]
  public void Parse_Slide_CarriesKindAndSlideNumber()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3");
    Assert.True(r.IsSuccess);
    PptxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.Slide, a.Kind),
        () => Assert.Equal(3, a.Slide),
        () => Assert.Empty(a.ShapePath),
        () => Assert.Equal(0, a.Row),
        () => Assert.Equal(0, a.Col),
        () => Assert.Equal(0, a.EffectIndex));
  }

  [Fact]
  public void Parse_Shape_CarriesTheShapePath()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3.sh2");
    Assert.True(r.IsSuccess);
    PptxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.Shape, a.Kind),
        () => Assert.Equal(3, a.Slide),
        () => Assert.Equal([2], a.ShapePath));
  }

  [Fact]
  public void Parse_GroupChild_CarriesEveryNestingLevel()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3.sh2.sh1");
    Assert.True(r.IsSuccess);
    PptxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.Shape, a.Kind),
        () => Assert.Equal([2, 1], a.ShapePath));
  }

  [Fact]
  public void Parse_TableCell_CarriesTheTablePathAndCoordinates()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3.sh2.t1.c4");
    Assert.True(r.IsSuccess);
    PptxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.TableCell, a.Kind),
        () => Assert.Equal(3, a.Slide),
        () => Assert.Equal([2], a.ShapePath),
        () => Assert.Equal(1, a.Row),
        () => Assert.Equal(4, a.Col));
  }

  [Fact]
  public void Parse_Notes_CarriesTheNotesKind()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3.notes");
    Assert.True(r.IsSuccess);
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.Notes, r.Value.Kind),
        () => Assert.Equal(3, r.Value.Slide));
  }

  [Fact]
  public void Parse_Animation_CarriesTheEffectIndex()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s3.anim1");
    Assert.True(r.IsSuccess);
    Assert.Multiple(
        () => Assert.Equal(PptxAddressKind.Animation, r.Value.Kind),
        () => Assert.Equal(1, r.Value.EffectIndex));
  }

  [Fact]
  public void Parse_LeadingZeros_ParseAndRenderCanonically()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s03");
    Assert.True(r.IsSuccess);
    Assert.Multiple(
        () => Assert.Equal(3, r.Value.Slide),
        () => Assert.Equal("s3", r.Value.Render()));
  }

  [Fact]
  public void Parse_LeadingZerosInsideAShapePath_RenderCanonically()
  {
    Result<PptxAddress> r = PptxAddress.Parse("s03.sh02.t01.c04");
    Assert.True(r.IsSuccess);
    Assert.Equal("s3.sh2.t1.c4", r.Value.Render());
  }

  // ---- Rejected: every grammar violation is InvalidAddress, never an exception ----

  [Theory]
  [InlineData("")]
  [InlineData(null)]
  [InlineData("s0")]
  [InlineData("s")]
  [InlineData("sx")]
  [InlineData("slide3")]
  [InlineData("S3")]
  [InlineData("s3.")]
  [InlineData(".s3")]
  [InlineData("s3..sh1")]
  [InlineData("s3.sh0")]
  [InlineData("s3.sh")]
  [InlineData("s3.shx")]
  [InlineData("s3.sh2.t1")]
  [InlineData("s3.sh2.t1.c4.extra")]
  [InlineData("s3.sh2.c4.t1")]
  [InlineData("s3.sh2.t0.c1")]
  [InlineData("s3.sh2.t1.c0")]
  [InlineData("s3.sh2.tx.c1")]
  [InlineData("s3.notes.extra")]
  [InlineData("s3.anim0")]
  [InlineData("s3.anim")]
  [InlineData("s3.animx")]
  [InlineData("s3.anim1.extra")]
  [InlineData("s3.middle")]
  [InlineData("s 3")]
  [InlineData("s3 ")]
  [InlineData(" s3")]
  [InlineData("s+3")]
  [InlineData("s99999999999999999999")]
  public void Parse_RejectsGrammarViolations_WithInvalidAddress(string? raw)
  {
    Result<PptxAddress> r = PptxAddress.Parse(raw);
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal(PptxErrorCodes.InvalidAddress, error.Code);
  }

  [Fact]
  public void Parse_Rejection_MessageNamesTheGrammar()
  {
    Result<PptxAddress> r = PptxAddress.Parse("slide3");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Multiple(
        () => Assert.Contains("s<N>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("s<N>.sh<M>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("s<N>.sh<M>.t<R>.c<C>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("s<N>.notes", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("s<N>.anim<K>", error.Message, StringComparison.Ordinal));
  }

  [Fact]
  public void Parse_EmptyText_NamesTheEmptyAddress()
  {
    Result<PptxAddress> r = PptxAddress.Parse("");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Contains("empty", error.Message, StringComparison.Ordinal);
  }

  // ---- Builders agree with the parser ----

  [Fact]
  public void Builders_RenderTheTextTheParserAccepts()
  {
    Assert.Multiple(
        () => Assert.Equal("s3", PptxAddress.ForSlide(3).Render()),
        () => Assert.Equal("s3.sh2", PptxAddress.ForShape(3, [2]).Render()),
        () => Assert.Equal("s3.sh2.sh1", PptxAddress.ForShape(3, [2, 1]).Render()),
        () => Assert.Equal("s3.sh2.t1.c4", PptxAddress.ForTableCell(3, [2], 1, 4).Render()),
        () => Assert.Equal("s3.notes", PptxAddress.ForNotes(3).Render()),
        () => Assert.Equal("s3.anim1", PptxAddress.ForAnimation(3, 1).Render()));
  }

  // ---- Programmer-error guards ----

  [Fact]
  public void ForSlide_SlideZero_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => PptxAddress.ForSlide(0));

  [Fact]
  public void ForShape_EmptyPath_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => PptxAddress.ForShape(1, []));

  [Fact]
  public void ForShape_ZeroIndexInPath_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => PptxAddress.ForShape(1, [0]));

  [Fact]
  public void ForTableCell_RowBelowOne_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => PptxAddress.ForTableCell(1, [1], 0, 1));

  [Fact]
  public void ForTableCell_ColBelowOne_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => PptxAddress.ForTableCell(1, [1], 1, 0));

  [Fact]
  public void ForAnimation_EffectIndexZero_Throws() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => PptxAddress.ForAnimation(1, 0));

  [Fact]
  public void Constructor_SlideAddressCarryingAShapePath_Throws() =>
      _ = Assert.Throws<ArgumentException>(() => new PptxAddress(PptxAddressKind.Slide, 1, [1], 0, 0, 0));
}
