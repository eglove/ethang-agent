using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the shared optional string-array parser.
/// Array-of-strings only; emptiness rules belong to callers.</summary>
public class ToolArgumentsOptionalStringArrayTests
{
  private static Result<IReadOnlyList<string>?> Parse(string json) =>
      ToolArguments.OptionalStringArray(JsonDocument.Parse(json).RootElement, "files");

  [Fact]
  public void AbsentKey_ReturnsNull()
  {
    Result<IReadOnlyList<string>?> r = Parse("{}");
    Assert.True(r.IsSuccess);
    Assert.Null(r.ValueOrNull);
  }

  [Fact]
  public void ArrayOfStrings_PassesVerbatim()
  {
    Result<IReadOnlyList<string>?> r = Parse(/*lang=json,strict*/ "{\"files\":[\"a.cs\",\"b.txt\"]}");
    Assert.True(r.IsSuccess);
    Assert.Equal(["a.cs", "b.txt"], r.ValueOrNull);
  }

  [Fact]
  public void EmptyArray_SucceedsWithZeroEntries()
  {
    Result<IReadOnlyList<string>?> r = Parse(/*lang=json,strict*/ "{\"files\":[]}");
    Assert.True(r.IsSuccess);
    Assert.Empty(r.ValueOrNull!);
  }

  [Theory]
  [InlineData("\"a.cs\"")]   // bare string
  [InlineData("[1,2]")]        // array of numbers
  [InlineData("[\"a\",2]")]   // mixed
  [InlineData(/*lang=json,strict*/ "{\"a\":1}")]    // object
  public void NonStringArrayOrNonArray_FailsWithTypeError(string raw)
  {
    Result<IReadOnlyList<string>?> r = Parse("{\"files\":" + raw + "}");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterType", r.Error.Code);
  }
}
