namespace eThangAgent.Desktop.Tests;

/// <summary>The DPAPI protector: plaintext keys must never survive into storage, and
///     undecryptable blobs read as "unconfigured" — never a crash.</summary>
public class DpapiKeyProtectorTests
{
  private readonly DpapiKeyProtector _protector = new();

  [Fact]
  public void Protect_Then_Unprotect_Round_Trips()
  {
    string stored = _protector.Protect("sk-or-v1-secret");

    Assert.Equal("sk-or-v1-secret", _protector.Unprotect(stored));
  }

  [Fact]
  public void Protect_Never_Returns_The_Plaintext()
  {
    string stored = _protector.Protect("sk-or-v1-secret");

    Assert.NotEqual("sk-or-v1-secret", stored);
    Assert.DoesNotContain("sk-or-v1-secret", stored, StringComparison.Ordinal);
  }

  [Fact]
  public void Unprotect_Corrupted_Blobs_Return_Null_Not_Throw()
  {
    Assert.Null(_protector.Unprotect("not base64 !!"));
    Assert.Null(_protector.Unprotect(Convert.ToBase64String([1, 2, 3, 4]))); // valid base64, not a DPAPI blob
  }
}
