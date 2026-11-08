namespace eThangAgent.ToolDomain.Tests;

/// <summary>Mechanical line-ending primitives: dominance detection over an existing
///     file's terminators and normalization of incoming text to a target convention.
///     Pure string work — no IO, no project knowledge.</summary>
public class LineEndingsTests
{
  [Fact]
  public void DetectDominant_AllLf_ReturnsLf() =>
      Assert.Equal(LineEnding.Lf, LineEndings.DetectDominant("a\nb\nc\n"));

  [Fact]
  public void DetectDominant_AllCrLf_ReturnsCrLf() =>
      Assert.Equal(LineEnding.CrLf, LineEndings.DetectDominant("a\r\nb\r\nc\r\n"));

  [Fact]
  public void DetectDominant_NoTerminators_ReturnsNull() =>
      Assert.Null(LineEndings.DetectDominant("single line without a terminator"));

  [Fact]
  public void DetectDominant_MixedWithoutMajority_ReturnsNull() =>
      // One LF line and one CRLF line: no convention is dominant, so the project's
      // convention decides rather than a coin flip.
      Assert.Null(LineEndings.DetectDominant("a\nb\r\nc"));

  [Fact]
  public void DetectDominant_MajorityWins()
  {
    Assert.Equal(LineEnding.CrLf, LineEndings.DetectDominant("a\r\nb\r\nc\r\nd\ne"));
    Assert.Equal(LineEnding.Lf, LineEndings.DetectDominant("a\nb\nc\nd\r\ne"));
  }

  [Fact]
  public void DetectDominant_BareCrCountsAsCrTerminated() =>
      Assert.Equal(LineEnding.CrLf, LineEndings.DetectDominant("a\rb\rc"));

  [Fact]
  public void Normalize_LfToCrLf_RewritesEveryTerminator() =>
      Assert.Equal("a\r\nb\r\nc", LineEndings.Normalize("a\nb\nc", LineEnding.CrLf));

  [Fact]
  public void Normalize_CrLfToLf_LeavesNoCarriageReturn()
  {
    string result = LineEndings.Normalize("a\r\nb\r\nc", LineEnding.Lf);
    Assert.Equal("a\nb\nc", result);
    Assert.DoesNotContain('\r', result);
  }

  [Fact]
  public void Normalize_BareCr_IsATerminator() =>
      Assert.Equal("a\r\nb", LineEndings.Normalize("a\rb", LineEnding.CrLf));

  [Fact]
  public void Normalize_MissingTrailingNewline_DoesNotAddOne() =>
      Assert.Equal("a\r\nb", LineEndings.Normalize("a\nb", LineEnding.CrLf));

  [Fact]
  public void Normalize_EmptyText_StaysEmpty() =>
      Assert.Equal("", LineEndings.Normalize("", LineEnding.CrLf));

  [Fact]
  public void EnsureFinalNewline_MissingTerminator_AppendsExactlyOne()
  {
    Assert.Equal("a\nb\n", LineEndings.EnsureFinalNewline("a\nb", LineEnding.Lf));
    Assert.Equal("a\nb\r\n", LineEndings.EnsureFinalNewline("a\nb", LineEnding.CrLf));
  }

  [Fact]
  public void EnsureFinalNewline_AlreadyTerminated_IsByteIdentical()
  {
    Assert.Equal("a\nb\n", LineEndings.EnsureFinalNewline("a\nb\n", LineEnding.Lf));
    Assert.Equal("a\nb\r\n", LineEndings.EnsureFinalNewline("a\nb\r\n", LineEnding.CrLf));
  }

  [Fact]
  public void EnsureFinalNewline_EmptyText_StaysEmpty() =>
      // An empty file has no final line to terminate, so the editorconfig setting does
      // not turn it into a one-byte file.
      Assert.Equal("", LineEndings.EnsureFinalNewline("", LineEnding.Lf));

  [Fact]
  public void Terminator_And_Label_AreTheDocumentedForms()
  {
    Assert.Equal("\n", LineEndings.Terminator(LineEnding.Lf));
    Assert.Equal("\r\n", LineEndings.Terminator(LineEnding.CrLf));
    Assert.Equal("LF", LineEndings.Label(LineEnding.Lf));
    Assert.Equal("CRLF", LineEndings.Label(LineEnding.CrLf));
  }
}
