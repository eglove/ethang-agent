using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Commit path security rules. Every rule is its own error, checked in
/// rule order; error codes are stable because they are the tool's feedback
/// contract to the model.</summary>
public class CommitFilePathsTests
{
  [Fact]
  public void NullList_Fails()
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create(null);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("files", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyList_Fails()
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create([]);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  [Fact]
  public void RelativePaths_SucceedAndRoundTripVerbatim()
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create(["src/A.cs", "docs/readme.md"]);
    Assert.True(r.IsSuccess);
    Assert.Equal(["src/A.cs", "docs/readme.md"], r.Value.Paths);
  }

  [Fact]
  public void SingleDotEntry_Fails()
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create(["."]);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  [Theory]
  [InlineData("..")]
  [InlineData("a/../../etc")]
  [InlineData("a/b/..")]
  public void TraversingSegment_Fails(string p)
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create([p]);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("'..'", r.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("C:\\x\\a.cs")]
  [InlineData("\\\\server\\share\\a.cs")]
  [InlineData("/abs/a.cs")]
  [InlineData("\\abs\\a.cs")]
  public void AbsolutePath_Fails(string p)
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create([p]);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("absolute", r.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void BlankEntry_Fails(string p)
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create([p]);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("non-empty", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Error_ReportsTheOffendingEntry()
  {
    Result<CommitFilePaths> r = CommitFilePaths.Create(["ok.cs", "../bad"]);
    Assert.False(r.IsSuccess);
    Assert.Contains("../bad", r.Error.Message, StringComparison.Ordinal);
  }
}
