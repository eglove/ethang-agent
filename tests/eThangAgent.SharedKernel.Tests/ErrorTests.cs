namespace eThangAgent.SharedKernel.Tests;

public class ErrorTests
{
    [Fact]
    public void Error_HoldsCodeAndMessage()
    {
        var error = new Error("TEST_CODE", "A test message");
        Assert.Equal("TEST_CODE", error.Code);
        Assert.Equal("A test message", error.Message);
    }

    [Fact]
    public void Error_Equal_WhenCodeAndMessageMatch()
    {
        var a = new Error("X", "msg");
        var b = new Error("X", "msg");
        Assert.Equal(a, b);
    }
}
