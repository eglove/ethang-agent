namespace eThangAgent.SharedKernel.Tests;

public class ResultTests
{
    [Fact]
    public void Success_HoldsValueAndIsSuccess()
    {
        var result = Result<int>.Success(42);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_HoldsErrorAndIsNotSuccess()
    {
        var error = new Error("FAIL", "something went wrong");
        var result = Result<int>.Failure(error);
        Assert.False(result.IsSuccess);
        Assert.Equal(default, result.Value);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Match_RoutesSuccess()
    {
        var result = Result<int>.Success(42);
        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
        Assert.Equal("ok:42", output);
    }

    [Fact]
    public void Match_RoutesFailure()
    {
        var result = Result<int>.Failure(new Error("X", "msg"));
        var output = result.Match(v => $"ok:{v}", e => $"err:{e.Code}");
        Assert.Equal("err:X", output);
    }

    [Fact]
    public void Map_TransformsSuccess()
    {
        var result = Result<int>.Success(21).Map(v => v * 2);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Map_PassesThroughFailure()
    {
        var error = new Error("X", "msg");
        var result = Result<int>.Failure(error).Map(v => v * 2);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Bind_ChainsSuccess()
    {
        var result = Result<int>.Success(21)
            .Bind(v => Result<int>.Success(v * 2));
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Bind_ShortCircuitsOnFailure()
    {
        var error = new Error("X", "msg");
        var called = false;
        var result = Result<int>.Failure(error)
            .Bind<int>(_ => { called = true; return Result<int>.Success(0); });
        Assert.False(result.IsSuccess);
        Assert.False(called);
    }
}
