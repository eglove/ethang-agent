using System.Text;

namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The PKCE material one authorization request carries: the challenge is the
///     S256 digest of the verifier, both are base64url without padding, and every attempt
///     gets fresh values.</summary>
public class OpenRouterPkceTests
{
  [Fact]
  public void Challenge_IsTheUnpaddedBase64UrlSha256OfTheVerifier()
  {
    // RFC 7636 Appendix B's worked example: the verifier's digest is fixed, so a change
    // to the encoding or the hash shows up here rather than at the provider.
    const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
        OpenRouterPkce.ChallengeFor(verifier));
  }

  [Fact]
  public void Create_ProducesUrlSafeValuesWithoutPadding()
  {
    OpenRouterPkce pkce = OpenRouterPkce.Create();

    Assert.DoesNotContain('=', pkce.Verifier);
    Assert.DoesNotContain('+', pkce.Verifier);
    Assert.DoesNotContain('/', pkce.Verifier);
    Assert.DoesNotContain('=', pkce.State);
    Assert.Equal(OpenRouterPkce.ChallengeFor(pkce.Verifier), pkce.Challenge);
    Assert.Equal("S256", OpenRouterPkce.ChallengeMethod);
  }

  [Fact]
  public void Create_ProducesADistinctVerifierAndStateEveryTime()
  {
    OpenRouterPkce first = OpenRouterPkce.Create();
    OpenRouterPkce second = OpenRouterPkce.Create();

    Assert.NotEqual(first.Verifier, second.Verifier);
    Assert.NotEqual(first.State, second.State);
  }

  [Fact]
  public void Create_DerivesTheChallengeFromTheSuppliedBytes()
  {
    byte[] verifier = Encoding.ASCII.GetBytes("fixed-verifier");
    byte[] state = Encoding.ASCII.GetBytes("fixed-state");

    OpenRouterPkce pkce = OpenRouterPkce.Create(verifier, state);

    // The verifier is the base64url encoding of the supplied bytes; the challenge is its
    // digest, so the pair stays consistent whatever entropy the caller injects.
    Assert.Equal(OpenRouterPkce.ChallengeFor(pkce.Verifier), pkce.Challenge);
    Assert.NotEqual(OpenRouterPkce.Create().Verifier, pkce.Verifier);
  }
}
