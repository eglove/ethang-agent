using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Strict input parsing for the read_image tool (issue #20): path required,
///     unknown parameters rejected, types exact - the same discipline as
///     ReadToolInput, pinned through its own suite.</summary>
public class ReadImageToolInputTests
{
  // JSON002 fires only in the format/IDE host; the pragma pair mirrors the repo's
  // standing pattern for literal JSON arguments in fixtures.
#pragma warning disable JSON002

  private static Result<ReadImageToolInput> Create(string json) => ReadImageToolInput.Create(json);

  [Fact]
  public void Create_ValidArgs_ParsesPath()
  {
    Result<ReadImageToolInput> r = Create("{\"timeoutSeconds\":60,\"path\":\"img/photo.png\"}");
    Assert.True(r.IsSuccess);
    Assert.Equal("img/photo.png", r.Value.Path);
  }

  [Fact]
  public void Create_MissingPath_FailsWithMissingParameter()
  {
    Result<ReadImageToolInput> r = Create("{\"timeoutSeconds\":60}");
    Assert.False(r.IsSuccess);
    Assert.Equal("MissingParameter", r.Error.Code);
    Assert.Contains("path", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_EmptyPath_Fails()
  {
    Result<ReadImageToolInput> r = Create("{\"timeoutSeconds\":60,\"path\":\"\"}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterValue, r.Error.Code);
  }

  [Fact]
  public void Create_PathWrongType_Fails()
  {
    Result<ReadImageToolInput> r = Create("{\"timeoutSeconds\":60,\"path\":123}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.InvalidParameterType, r.Error.Code);
  }

  [Fact]
  public void Create_MissingTimeout_ParsesAtInputLayer()
  {
    // The timeout budget is the TOOL ENVELOPE's requirement (ToolCallEnvelopeParser),
    // not the input record's - ReadToolInput sets the same precedent. The input layer
    // admits it; the tool's envelope parse rejects a missing budget.
    Result<ReadImageToolInput> r = Create("{" + "\"path\":\"a.png\"" + "}");
    Assert.True(r.IsSuccess);
    Assert.Equal("a.png", r.Value.Path);
  }

  [Fact]
  public void Create_UnknownParameter_Fails()
  {
    Result<ReadImageToolInput> r = Create("{\"timeoutSeconds\":60,\"path\":\"a.png\",\"width\":100}");
    Assert.False(r.IsSuccess);
    Assert.Equal(ToolErrorCodes.UnknownParameter, r.Error.Code);
    Assert.Contains("width", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_NotJson_Fails() => Assert.False(Create("not json").IsSuccess);
#pragma warning restore JSON002
}
