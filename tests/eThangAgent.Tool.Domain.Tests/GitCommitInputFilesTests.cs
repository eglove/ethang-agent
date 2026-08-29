using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>files-parameter parsing: shape via the shared array helper,
/// semantics delegated to CommitFilePaths.Create.</summary>
public class GitCommitInputFilesTests
{
  [Fact]
  public void FilesOmitted_ParsesWithNullPaths()
  {
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"None","description":"x"}""");
    Assert.True(r.IsSuccess);
    Assert.Null(r.Value.Files);
  }

  [Fact]
  public void FilesProvided_ParsesIntoValidatedPaths()
  {
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"None","description":"x","files":["src/A.cs"]}""");
    Assert.True(r.IsSuccess);
    Assert.NotNull(r.Value.Files);
    Assert.Equal(["src/A.cs"], r.Value.Files.Paths);
  }

  [Fact]
  public void FilesWrongShape_FailsWithTypeError()
  {
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"None","description":"x","files":"src/A.cs"}""");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterType", r.Error.Code);
  }

  [Fact]
  public void FilesTraversingPath_FailsWithValueError_FromValueObject()
  {
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"None","description":"x","files":["../x"]}""");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("../x", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void FilesEmptyArray_FailsWithValueError()
  {
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"None","description":"x","files":[]}""");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  [Fact]
  public void FilesValidatedAtInputLevel_StyleLegalityStaysWithCommitMessage()
  {
    // Input-level rule: files paths are validated here (CommitFilePaths), while
    // style legality is CommitMessage's concern — a bogus style still parses.
    Result<GitCommitInput> r = GitCommitInput.Create(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"style":"Bogus","description":"x","files":["../x"]}""");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
    Assert.Contains("../x", r.Error.Message, StringComparison.Ordinal);
  }
}
