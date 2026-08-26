namespace eThangAgent.SharedKernel.Tests;

public class ErrorTests
{
  [Fact]
  public void DomainError_HoldsCodeAndMessage()
  {
    DomainError error = new("TEST_CODE", "A test message");
    Assert.Equal("TEST_CODE", error.Code);
    Assert.Equal("A test message", error.Message);
  }

  [Fact]
  public void DomainError_Equal_WhenCodeAndMessageMatch()
  {
    DomainError a = new("X", "msg");
    DomainError b = new("X", "msg");
    Assert.Equal(a, b);
  }
}
