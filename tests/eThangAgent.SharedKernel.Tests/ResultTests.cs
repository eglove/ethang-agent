namespace eThangAgent.SharedKernel.Tests;

public class ResultTests
{
  [Fact]
  public void Success_HoldsValueAndIsSuccess()
  {
    Result<int> result = Result.Success(42);
    Assert.True(result.IsSuccess);
    Assert.Equal(42, result.Value);
    Assert.Null(result.Error);
  }

  [Fact]
  public void Failure_HoldsErrorAndIsNotSuccess()
  {
    DomainError error = new("FAIL", "something went wrong");
    Result<int> result = Result.Failure<int>(error);
    Assert.False(result.IsSuccess);
    Assert.Equal(default, result.Value);
    Assert.Equal(error, result.Error);
  }

  [Fact]
  public void Match_RoutesSuccess()
  {
    Result<int> result = Result.Success(42);
    string output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
    Assert.Equal("ok:42", output);
  }

  [Fact]
  public void Match_RoutesFailure()
  {
    Result<int> result = Result.Failure<int>(new DomainError("X", "msg"));
    string output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
    Assert.Equal("err:X", output);
  }

  [Fact]
  public void Map_TransformsSuccess()
  {
    Result<int> result = Result.Success(21).Map(v => v * 2);
    Assert.True(result.IsSuccess);
    Assert.Equal(42, result.Value);
  }

  [Fact]
  public void Map_PassesThroughFailure()
  {
    DomainError error = new("X", "msg");
    Result<int> result = Result.Failure<int>(error).Map(v => v * 2);
    Assert.False(result.IsSuccess);
    Assert.Equal(error, result.Error);
  }

  [Fact]
  public void Bind_ChainsSuccess()
  {
    Result<int> result = Result.Success(21)
        .Bind(v => Result.Success(v * 2));
    Assert.True(result.IsSuccess);
    Assert.Equal(42, result.Value);
  }

  [Fact]
  public void Bind_ShortCircuitsOnFailure()
  {
    DomainError error = new("X", "msg");
    bool called = false;
    Result<int> result = Result.Failure<int>(error)
        .Bind(_ =>
        {
          called = true;
          return Result.Success(0);
        });
    Assert.False(result.IsSuccess);
    Assert.False(called);
  }
}
