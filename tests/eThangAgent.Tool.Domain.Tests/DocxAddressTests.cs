using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the docx address grammar (task 1): the pinned
///     address shapes every later docx task resolves, plus the model-visible
///     FileVersion token the stale-edit guard encodes and decodes. Every failure
///     carries the exact pinned code (InvalidAddress / InvalidParameterValue) and
///     a message that tells the model how to fix the input.</summary>
public class DocxAddressTests
{
  // ---- Accepted shapes ----

  [Fact]
  public void Parse_Paragraph_CarriesKindBodyIndexAndRaw()
  {
    Result<DocxAddress> r = DocxAddress.Parse("p12");
    Assert.True(r.IsSuccess);
    DocxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(DocxAddressKind.Paragraph, a.Kind),
        () => Assert.Equal(12, a.BodyIndex),
        () => Assert.Equal("p12", a.Raw));
  }

  [Fact]
  public void Parse_Table_CarriesKindAndBodyIndex()
  {
    Result<DocxAddress> r = DocxAddress.Parse("t3");
    Assert.True(r.IsSuccess);
    DocxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(DocxAddressKind.Table, a.Kind),
        () => Assert.Equal(3, a.BodyIndex),
        () => Assert.Equal(0, a.Row),
        () => Assert.Equal(0, a.Col),
        () => Assert.Equal(0, a.Section),
        () => Assert.Equal("t3", a.Raw));
  }

  [Fact]
  public void Parse_Cell_CarriesTableRowAndCol()
  {
    Result<DocxAddress> r = DocxAddress.Parse("t3.r2.c1");
    Assert.True(r.IsSuccess);
    DocxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(DocxAddressKind.TableCell, a.Kind),
        () => Assert.Equal(3, a.BodyIndex),
        () => Assert.Equal(2, a.Row),
        () => Assert.Equal(1, a.Col),
        () => Assert.Equal("t3.r2.c1", a.Raw));
  }

  [Fact]
  public void Parse_HeaderParagraph_CarriesSectionAndParagraphIndex()
  {
    Result<DocxAddress> r = DocxAddress.Parse("sec1.header.p2");
    Assert.True(r.IsSuccess);
    DocxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(DocxAddressKind.HeaderParagraph, a.Kind),
        () => Assert.Equal(1, a.Section),
        () => Assert.Equal(2, a.BodyIndex),
        () => Assert.Equal("sec1.header.p2", a.Raw));
  }

  [Fact]
  public void Parse_FooterParagraph_CarriesSectionAndParagraphIndex()
  {
    Result<DocxAddress> r = DocxAddress.Parse("sec2.footer.p7");
    Assert.True(r.IsSuccess);
    DocxAddress a = r.Value;
    Assert.Multiple(
        () => Assert.Equal(DocxAddressKind.FooterParagraph, a.Kind),
        () => Assert.Equal(2, a.Section),
        () => Assert.Equal(7, a.BodyIndex),
        () => Assert.Equal("sec2.footer.p7", a.Raw));
  }

  [Fact]
  public void Parse_IndexOne_IsTheFirstParagraph()
  {
    Result<DocxAddress> r = DocxAddress.Parse("p1");
    Assert.True(r.IsSuccess);
    Assert.Equal(1, r.Value.BodyIndex);
  }

  // ---- Rejected: every grammar violation is InvalidAddress ----

  [Theory]
  [InlineData("p0")]             // 1-based: 0 invalid
  [InlineData("p")]
  [InlineData("px")]
  [InlineData("t1.r0.c1")]       // row 0 invalid
  [InlineData("t1.r2")]          // cell needs row AND col
  [InlineData("t1.r2.c0")]       // col 0 invalid
  [InlineData("t0")]             // table 0 invalid
  [InlineData("sec0.header.p1")] // section 0 invalid
  [InlineData("sec1.header")]    // header needs a paragraph index
  [InlineData("sec1.footer")]    // footer needs a paragraph index
  [InlineData("sec1.middle.p1")] // middle segment must be header|footer
  [InlineData("sec1.header.p0")] // paragraph index 0 invalid
  [InlineData("sec1.header.p1.x")] // extra segments
  [InlineData("P12")]            // case-sensitive grammar
  [InlineData("SEC1.header.p1")] // case-sensitive grammar
  [InlineData("p1.")]            // trailing dot is an empty segment
  [InlineData(".p1")]            // leading dot is an empty segment
  [InlineData(".r2.c1")]         // empty head segment (3-segment cell shape)
  [InlineData("t1..c1")]         // empty middle segment (cell shape)
  [InlineData("t1.r2.")]         // empty tail segment (cell shape)
  [InlineData("t1..")]           // two empty segments (cell shape)
  [InlineData("t..")]            // empty segments after bare t
  [InlineData("..")]             // every segment empty
  [InlineData("sec1.header.")]   // empty tail segment (layered shape)
  [InlineData("sec1.footer.")]   // empty tail segment (footer shape)
  [InlineData(".header.p1")]     // empty head segment (layered shape)
  [InlineData("sec1..p1")]       // empty middle segment (layered shape)
  [InlineData("tx.r2.c1")]       // cell head is not an integer
  [InlineData("t.r2.c1")]        // cell head is missing entirely
  [InlineData("t0.r2.c1")]       // cell head is 0 (1-based grammar)
  [InlineData("t-1.r2.c1")]      // cell head is negative
  [InlineData("t99999999999999999999.r2.c1")] // cell head overflows int
  [InlineData("p 1")]            // space inside the index
  [InlineData("p1 ")]            // no trimming
  [InlineData(" p1")]            // no trimming
  [InlineData("p+1")]            // int.TryParse rejects the sign-plus form
  [InlineData("p１２")]            // full-width digits are not ASCII ints
  [InlineData("")]
  [InlineData(null)]
  public void Parse_RejectsGrammarViolations(string? raw)
  {
    Result<DocxAddress> r = DocxAddress.Parse(raw);
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal(DocxErrorCodes.InvalidAddress, error.Code);
  }

  [Fact]
  public void Parse_Rejection_MessageNamesTheGrammar()
  {
    Result<DocxAddress> r = DocxAddress.Parse("px");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Multiple(
        () => Assert.Contains("p<N>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("t<N>.r<R>.c<C>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("sec<S>.header.p<N>", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("sec<S>.footer.p<N>", error.Message, StringComparison.Ordinal));
  }

  // ---- FileVersionToken: encode / decode round trip ----

  [Fact]
  public void Token_RoundTrip_PreservesLengthAndTicks()
  {
    FileVersion v = new(1234, new DateTime(638000000000000000, DateTimeKind.Utc));
    string token = FileVersionToken.Encode(v);
    Result<FileVersion> r = FileVersionToken.Decode(token);
    Assert.True(r.IsSuccess);
    FileVersion back = r.Value;
    Assert.Multiple(
        () => Assert.Equal(v.Length, back.Length),
        () => Assert.Equal(v.LastWriteTimeUtc.Ticks, back.LastWriteTimeUtc.Ticks));
  }

  [Fact]
  public void Token_Encode_HasThePinnedShape()
  {
    FileVersion v = new(42, new DateTime(638000000000000001, DateTimeKind.Utc));
    string token = FileVersionToken.Encode(v);
    Assert.Equal("v42:638000000000000001", token);
  }

  [Fact]
  public void Token_Decode_AcceptsThePinnedShape()
  {
    Result<FileVersion> r = FileVersionToken.Decode("v42:638000000000000001");
    Assert.True(r.IsSuccess);
    FileVersion v = r.Value;
    Assert.Multiple(
        () => Assert.Equal(42, v.Length),
        () => Assert.Equal(638000000000000001L, v.LastWriteTimeUtc.Ticks));
  }

  [Theory]
  [InlineData("vabc")]
  [InlineData("v1:")]
  [InlineData("1:2")]
  [InlineData("v-1:0")]
  [InlineData("")]
  [InlineData(null)]
  public void Token_Decode_RejectsGarbage_WithInvalidParameterValue(string? token)
  {
    Result<FileVersion> r = FileVersionToken.Decode(token);
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, error.Code);
  }

  [Fact]
  public void Token_Decode_RejectsNegativeLength()
  {
    Result<FileVersion> r = FileVersionToken.Decode("v-1:638000000000000001");
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, error.Code);
  }

  [Fact]
  public void Token_Decode_RejectsNonPositiveTicks()
  {
    Result<FileVersion> r = FileVersionToken.Decode("v1:0");
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, error.Code);
  }

  [Fact]
  public void Token_Decode_Rejection_MessageNamesTheExpectedShape()
  {
    Result<FileVersion> r = FileVersionToken.Decode("vabc");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Contains("v<length>:<ticks>", error.Message, StringComparison.Ordinal);
  }
}
