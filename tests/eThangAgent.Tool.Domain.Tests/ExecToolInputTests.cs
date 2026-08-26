using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecToolInputTests
{
  [Theory]
  [InlineData("not json")]
  [InlineData("[]")]
  [InlineData("\"hi\"")]
  public void NonObjectJson_IsRejected_InvalidJsonArguments(string json)
  {
    Result<ExecToolInput> result = ExecToolInput.Create(json);
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidJsonArguments", result.Error!.Code);
  }

  [Fact]
  public void UnknownParameter_IsRejected()
  {
    Result<ExecToolInput> result = ExecToolInput.Create(/*lang=json,strict*/ "{\"program\":\"x\",\"timeout\":5}");
    Assert.False(result.IsSuccess);
    Assert.Equal("UnknownParameter", result.Error!.Code);
    Assert.Contains("timeout", result.Error!.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingProgram_IsRejected()
  {
    Result<ExecToolInput> result = ExecToolInput.Create("{}");
    Assert.False(result.IsSuccess);
    Assert.Equal("MissingParameter", result.Error!.Code);
  }

  [Fact]
  public void NonStringProgram_IsRejected()
  {
    Result<ExecToolInput> result = ExecToolInput.Create(/*lang=json,strict*/ "{\"program\":42}");
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidParameterType", result.Error!.Code);
  }

  [Fact]
  public void EmptyStringProgram_IsRejected()
  {
    Result<ExecToolInput> result = ExecToolInput.Create(/*lang=json,strict*/ "{\"program\":\"\"}");
    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidParameterValue", result.Error!.Code);
  }

  [Fact]
  public void ValidJson_CreatesExecToolInput()
  {
    Result<ExecToolInput> result = ExecToolInput.Create(/*lang=json,strict*/ "{\"program\":\"Write-Output 'hi'\"}");
    Assert.True(result.IsSuccess);
    Assert.Equal("Write-Output 'hi'", result.Value!.Program);
  }
}
